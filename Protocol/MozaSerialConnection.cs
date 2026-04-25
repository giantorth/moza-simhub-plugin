using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections;
using System.IO.Ports;
using System.Linq;
using System.Reflection;
using System.Threading;


namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Stream identifier for coalescing latest-wins telemetry frames. Each kind has
    /// a single slot in <see cref="MozaSerialConnection"/>; enqueueing a new frame
    /// for the same kind overwrites any not-yet-sent predecessor, so stale frames
    /// can never pile up behind newer ones. One-shot/session traffic should still
    /// use <see cref="MozaSerialConnection.Send(byte[])"/>.
    /// </summary>
    public enum StreamKind
    {
        TierDash0 = 0,
        TierDash1 = 1,
        TierDash2 = 2,
        TierDash3 = 3,
        TierDash4 = 4,
        TierDash5 = 5,
        TierDash6 = 6,
        TierDash7 = 7,
        Enable = 8,
        Sequence = 9,
        Mode = 10,
    }

    public class MozaSerialConnection : IDisposable
    {
        private const int StreamSlotCount = 11;

        // Filter applied during port discovery. Receives the WMI-reported PID
        // ("0x1000") or null for probe-based discovery where PID is unknown.
        // Returns true to accept, false to skip. Default = accept all.
        private readonly Func<string?, bool>? _pidFilter;

        private volatile SerialPort? _port;
        private Thread? _readThread;
        private Thread? _writeThread;
        // One-shot lane: session traffic, settings writes, probes — must preserve
        // FIFO order and receives 4 ms pacing when bursted (Moza bases drop rapid
        // settings writes otherwise; see WriteLoop).
        private readonly ConcurrentQueue<byte[]> _oneShotQueue = new ConcurrentQueue<byte[]>();
        // Stream lane: per-kind latest-wins slots for periodic telemetry. Drained
        // after the one-shot lane, unpaced. SendStream overwrites any pending
        // slot so lagging frames never reach the wire stale.
        private readonly byte[]?[] _streamSlots = new byte[StreamSlotCount][];
        private volatile bool _running;
        private readonly object _lock = new object();
        private string? _lastPortName;
        private volatile bool _shutdownRequested;

        // Consecutive I/O error tracking. After sleep/resume the SerialPort handle
        // stays .IsOpen==true but every read/write throws IOException("Not ready"),
        // so nothing triggers reconnect. Count failures and force-close at threshold.
        private int _consecutiveIoErrors;
        private volatile bool _portFailureLogged;
        private const int PortDeadThreshold = 10;

        public event Action<byte[]>? MessageReceived;
        public bool IsConnected => _port?.IsOpen == true;

        /// <summary>
        /// The HID Product ID discovered from WMI during device enumeration.
        /// Null if PID could not be determined (e.g. probe-based discovery under Wine).
        /// </summary>
        public string? DiscoveredPid { get; private set; }

        public MozaSerialConnection() : this(null) { }

        /// <summary>
        /// Construct a serial connection scoped to a subset of MOZA composite
        /// devices. <paramref name="pidFilter"/> receives each candidate port's
        /// PID (null under Wine probe discovery) and returns true to accept.
        /// </summary>
        public MozaSerialConnection(Func<string?, bool>? pidFilter)
        {
            _pidFilter = pidFilter;
        }

        public bool Connect()
        {
            if (_shutdownRequested)
                return false;

            // Tear down any stale threads/port from a previous dead session
            // (e.g. after sleep/resume killed the tty but handle stayed open).
            if (_running || _port != null)
                Disconnect();

            // Try the last known port first to avoid re-probing (which
            // opens/closes the port and can reset the device under Wine).
            if (_lastPortName != null && TryOpen(_lastPortName))
                return true;

            var (portName, pid) = FindMozaPort(_pidFilter, () => _shutdownRequested);
            if (portName == null)
                return false;

            if (pid != null)
                DiscoveredPid = pid;

            return TryOpen(portName);
        }

        private bool TryOpen(string portName)
        {
            // Drain any stale messages from a previous connection
            while (_oneShotQueue.TryDequeue(out _)) { }
            for (int k = 0; k < _streamSlots.Length; k++)
                Interlocked.Exchange(ref _streamSlots[k], null);

            try
            {
                _port = new SerialPort(portName, MozaProtocol.BaudRate)
                {
                    ReadTimeout = 500,
                    WriteTimeout = 500,
                    // Larger buffers cushion bursts under Wine/tty0tty where
                    // the kernel-side pty queue can hold multiple concurrent
                    // device-session chunks (session 0x09 configJson state
                    // burst is up to 7 × 68B = ~500B in under 40ms).
                    ReadBufferSize = 65536,
                    WriteBufferSize = 16384,
                };
                _port.Open();
                _port.DiscardInBuffer();
                _port.DiscardOutBuffer();

                _running = true;
                _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "MozaSerialRead" };
                _writeThread = new Thread(WriteLoop) { IsBackground = true, Name = "MozaSerialWrite" };

                try
                {
                    _readThread.Start();
                    _writeThread.Start();
                }
                catch
                {
                    // If either start failed, tear down: signal stop, join whichever started,
                    // close port, then rethrow so the outer catch logs it.
                    _running = false;
                    try { _readThread?.Join(500); } catch { }
                    try { _writeThread?.Join(500); } catch { }
                    try { _port?.Close(); } catch { }
                    _port = null;
                    throw;
                }

                _lastPortName = portName;
                Interlocked.Exchange(ref _consecutiveIoErrors, 0);
                _portFailureLogged = false;
                SimHub.Logging.Current.Info($"[Moza] Connected to {portName}");
                return true;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"[Moza] Failed to connect to {portName}: {ex.Message}");
                return false;
            }
        }

        public void Disconnect()
        {
            _running = false;

            // Close the port BEFORE joining threads so any read/write blocked
            // in a kernel syscall on a wedged tty (sleep/resume, unplug, or
            // device-side stall) returns with an error and the thread can
            // exit. If we joined first, a wedged Write would sit holding no
            // lock but still pinned in the syscall; Join would hit its 1000ms
            // timeout and we'd proceed to Close, but SimHub's End() timeout
            // on the surrounding call would already have expired.
            SerialPort? p;
            lock (_lock)
            {
                p = _port;
                _port = null;
            }
            if (p != null)
            {
                try { p.Close(); }
                catch (Exception ex) { SimHub.Logging.Current.Debug($"[Moza] Port close: {ex.Message}"); }
            }

            _readThread?.Join(1000);
            _writeThread?.Join(1000);
        }

        public void Send(byte[] message)
        {
            if (message != null)
                _oneShotQueue.Enqueue(message);
        }

        /// <summary>
        /// Enqueue a periodic-stream frame with latest-wins coalescing. Any frame
        /// already pending in the same <see cref="StreamKind"/> slot is discarded.
        /// Use for telemetry/enable/sequence/mode — frames that only matter at their
        /// latest value. For ordered one-shot traffic use <see cref="Send(byte[])"/>.
        /// </summary>
        public void SendStream(StreamKind kind, byte[] message)
        {
            if (message == null) return;
            int idx = (int)kind;
            if ((uint)idx >= (uint)_streamSlots.Length) return;
            Interlocked.Exchange(ref _streamSlots[idx], message);
        }

        /// <summary>
        /// Drop everything pending: one-shot FIFO, all stream slots, and the OS
        /// write buffer. Called from TelemetrySender.Stop so clicking the test
        /// button's Stop halts the wheel immediately instead of bleeding through
        /// the ~16 KB WriteBufferSize (~1.4 s at 115200 baud).
        /// </summary>
        public void FlushPendingWrites()
        {
            while (_oneShotQueue.TryDequeue(out _)) { }
            for (int k = 0; k < _streamSlots.Length; k++)
                Interlocked.Exchange(ref _streamSlots[k], null);
            lock (_lock)
            {
                try { _port?.DiscardOutBuffer(); }
                catch (Exception ex) { SimHub.Logging.Current.Debug($"[Moza] DiscardOutBuffer: {ex.Message}"); }
            }
        }

        // Record an I/O failure. Throttles log spam and force-closes the port
        // once the failure count crosses the threshold so the reconnect timer
        // can reopen it (handles sleep/resume where .IsOpen stays true on dead tty).
        private void HandleIoFailure(string label, Exception ex)
        {
            if (!_running) return;

            int count = Interlocked.Increment(ref _consecutiveIoErrors);

            if (!_portFailureLogged)
            {
                SimHub.Logging.Current.Error($"[Moza] {label} error: {ex.GetType().Name}: {ex.Message}");
            }

            if (count >= PortDeadThreshold && !_portFailureLogged)
            {
                _portFailureLogged = true;
                SimHub.Logging.Current.Warn(
                    $"[Moza] Port wedged after {count} consecutive I/O errors — closing for reconnect");
                lock (_lock)
                {
                    try { _port?.Close(); } catch { }
                    _port = null;
                }
                while (_oneShotQueue.TryDequeue(out _)) { }
                for (int k = 0; k < _streamSlots.Length; k++)
                    Interlocked.Exchange(ref _streamSlots[k], null);
            }
        }

        private void ReadLoop()
        {
            SimHub.Logging.Current.Info("[Moza] Read thread started");
            int messageCount = 0;
            // Bulk read buffer — drains all available bytes from the OS read
            // buffer in one SerialPort.Read() call, then parses frames from
            // this byte array in memory. Per-byte ReadByte() under Wine/tty0tty
            // had ~100μs per-call overhead which made multi-chunk burst pacing
            // marginal even for valid frames.
            var rx = new List<byte>(capacity: 8192);
            var tmp = new byte[4096];

            while (_running)
            {
                try
                {
                    var port = _port;
                    if (port == null || !port.IsOpen)
                    {
                        Thread.Sleep(100);
                        continue;
                    }

                    // Wine's SerialPort.Read blocks for full count (doesn't
                    // return early when fewer bytes available), so we can't use
                    // a plain blocking Read. Instead poll BytesToRead and pull
                    // whatever is available. 2 ms sleep when idle keeps CPU
                    // usage negligible while still draining the pty buffer
                    // promptly — observed 1-15 bytes per pull at 115200 baud.
                    int avail = port.BytesToRead;
                    if (avail == 0)
                    {
                        Thread.Sleep(2);
                        continue;
                    }
                    if (avail > tmp.Length) avail = tmp.Length;
                    int n = port.Read(tmp, 0, avail);
                    if (n <= 0) continue;
                    for (int i = 0; i < n; i++)
                        rx.Add(tmp[i]);
                    Interlocked.Exchange(ref _consecutiveIoErrors, 0);

                    // Parse as many complete frames from `rx` as possible, then
                    // keep any trailing partial frame for the next bulk read.
                    int cursor = 0;
                    while (cursor < rx.Count)
                    {
                        int frameStart = cursor;
                        // Scan for frame start 0x7E
                        while (frameStart < rx.Count && rx[frameStart] != MozaProtocol.MessageStart)
                            frameStart++;
                        if (frameStart >= rx.Count)
                        {
                            // No start byte found at all — discard junk.
                            cursor = rx.Count;
                            break;
                        }
                        // Need at least start + length byte to proceed.
                        if (frameStart + 1 >= rx.Count)
                        {
                            cursor = frameStart;
                            break;
                        }
                        int payloadLength = rx[frameStart + 1];
                        if (payloadLength < 2 || payloadLength > 64)
                        {
                            // Invalid length — skip this start byte and resync on
                            // the next 0x7E. Common at connect when junk precedes
                            // real frames.
                            cursor = frameStart + 1;
                            continue;
                        }
                        int needed = payloadLength + 3; // group + device + payload + checksum
                        // Walk wire bytes starting after [start, len], collapsing
                        // 0x7E 0x7E wire pairs back to a single 0x7E body byte.
                        var raw = new byte[needed];
                        int decoded = 0;
                        int wirePos = frameStart + 2;
                        bool frameError = false;
                        bool needMoreData = false;
                        while (decoded < needed)
                        {
                            if (wirePos >= rx.Count) { needMoreData = true; break; }
                            byte wb = rx[wirePos++];
                            if (wb == MozaProtocol.MessageStart)
                            {
                                if (wirePos >= rx.Count) { needMoreData = true; break; }
                                byte esc = rx[wirePos++];
                                if (esc != MozaProtocol.MessageStart)
                                {
                                    frameError = true;
                                    break;
                                }
                                raw[decoded++] = MozaProtocol.MessageStart;
                            }
                            else
                            {
                                raw[decoded++] = wb;
                            }
                        }
                        if (needMoreData)
                        {
                            // Frame straddles buffer end; wait for more bytes.
                            cursor = frameStart;
                            break;
                        }
                        if (frameError || decoded != needed)
                        {
                            int nn = Math.Min(8, Math.Max(0, decoded));
                            string first8a = nn > 0 ? BitConverter.ToString(raw, 0, nn) : "(empty)";
                            SimHub.Logging.Current.Info(
                                $"[Moza] DROP frame-error: decoded={decoded}/{needed} len={payloadLength} first8={first8a}");
                            // Skip past the bad start byte and try to resync.
                            cursor = frameStart + 1;
                            continue;
                        }

                        // Validate wire-level checksum (includes 0x7E escape
                        // accounting per doc § 54).
                        var wireFrame = new byte[2 + payloadLength + 2];
                        wireFrame[0] = MozaProtocol.MessageStart;
                        wireFrame[1] = (byte)payloadLength;
                        Array.Copy(raw, 0, wireFrame, 2, payloadLength + 2);
                        byte expected = MozaProtocol.CalculateWireChecksum(wireFrame);
                        byte actual = raw[raw.Length - 1];
                        if (expected != actual)
                        {
                            int nn = Math.Min(8, raw.Length);
                            string first8a = nn > 0 ? BitConverter.ToString(raw, 0, nn) : "(empty)";
                            SimHub.Logging.Current.Info(
                                $"[Moza] DROP checksum mismatch: expected=0x{expected:X2} actual=0x{actual:X2} " +
                                $"len={payloadLength} group=0x{raw[0]:X2} dev=0x{raw[1]:X2} first8={first8a}");
                            cursor = frameStart + 1;
                            continue;
                        }

                        // Strip the checksum byte before passing to the parser.
                        var data = new byte[needed - 1];
                        Array.Copy(raw, 0, data, 0, data.Length);

                        messageCount++;
                        if (messageCount <= 5)
                        {
                            SimHub.Logging.Current.Info(
                                $"[Moza] Received msg #{messageCount}: len={payloadLength} " +
                                $"group=0x{data[0]:X2} dev=0x{data[1]:X2} ({data.Length} bytes)");
                        }
                        // Diagnostic: per-chunk log for 0xC3/71/7C/00 SerialStream
                        // frames so we can verify session 0x09 chunk reception.
                        if (data.Length >= 8 && data[0] == 0xC3 && data[1] == 0x71 &&
                            data[2] == 0x7C && data[3] == 0x00)
                        {
                            byte sess = data[4];
                            byte type = data[5];
                            int seqWire = data[6] | (data[7] << 8);
                            int bodyLen = data.Length - 8;
                            string first8 = bodyLen > 0
                                ? BitConverter.ToString(data, 8, Math.Min(8, bodyLen))
                                : "(empty)";
                            SimHub.Logging.Current.Info(
                                $"[Moza] WIRE sess=0x{sess:X2} type=0x{type:X2} seq={seqWire} " +
                                $"totalLen={data.Length} payload={bodyLen}B first8={first8}");
                        }
                        MessageReceived?.Invoke(data);
                        // Move cursor past the consumed wire bytes.
                        cursor = wirePos;
                    }
                    // Drop consumed bytes so `rx` doesn't grow unbounded.
                    if (cursor > 0)
                    {
                        if (cursor >= rx.Count)
                            rx.Clear();
                        else
                            rx.RemoveRange(0, cursor);
                    }
                }
                catch (TimeoutException)
                {
                    // Normal timeout under Wine, continue
                }
                catch (Exception ex)
                {
                    HandleIoFailure("Read", ex);
                    Thread.Sleep(100);
                }
            }
        }

        private void WriteLoop()
        {
            SimHub.Logging.Current.Info("[Moza] Write thread started");
            int writeCount = 0;
            // Pooled stuffing buffer. Worst-case stuffed size is 2 * decoded size;
            // grows on demand if a larger frame arrives.
            byte[] stuffBuf = new byte[512];
            // Monotonic 64-bit clock for write pacing. Replaces Environment.TickCount
            // (signed Int32, wraps every ~24.8 days) so the 4ms gate stays correct
            // across long uptime — Stopwatch.GetTimestamp ticks at high resolution
            // and never wraps for any plausible session length.
            long stopwatchFreq = System.Diagnostics.Stopwatch.Frequency;
            long fourMsTicks = stopwatchFreq * 4 / 1000;
            long lastWriteTs = System.Diagnostics.Stopwatch.GetTimestamp() - stopwatchFreq;
            bool lastWasOneShot = false;

            while (_running)
            {
                bool didWork = false;

                // 1) One-shot FIFO: session traffic, settings writes, probes.
                //    Paced at 4 ms between consecutive one-shots — Moza bases drop
                //    settings writes when flooded (ApplyProfile sends 30+ in a burst).
                //    Pacing is skipped when the previous write was a stream frame,
                //    since telemetry-group writes never trigger the drop.
                if (_oneShotQueue.TryDequeue(out var msg))
                {
                    if (lastWasOneShot)
                    {
                        long sinceTicks = System.Diagnostics.Stopwatch.GetTimestamp() - lastWriteTs;
                        if (sinceTicks < fourMsTicks)
                        {
                            int sleepMs = (int)((fourMsTicks - sinceTicks) * 1000 / stopwatchFreq);
                            if (sleepMs > 0) Thread.Sleep(sleepMs);
                        }
                    }
                    if (WriteFrame(msg, ref stuffBuf))
                    {
                        writeCount++;
                        if (writeCount <= 5)
                            SimHub.Logging.Current.Info($"[Moza] Sent cmd #{writeCount}: {msg.Length} bytes, group=0x{(msg.Length > 2 ? msg[2] : 0):X2}");
                        lastWriteTs = System.Diagnostics.Stopwatch.GetTimestamp();
                        lastWasOneShot = true;
                    }
                    didWork = true;
                }
                else
                {
                    // 2) Stream lane: latest-wins per kind, drained unpaced so a full
                    //    telemetry tick (dash + enable + sequence + mode) goes out
                    //    back-to-back in ~1 ms total instead of 12–28 ms of sleep.
                    for (int k = 0; k < _streamSlots.Length; k++)
                    {
                        var slot = Interlocked.Exchange(ref _streamSlots[k], null);
                        if (slot == null) continue;
                        if (WriteFrame(slot, ref stuffBuf))
                        {
                            writeCount++;
                            lastWriteTs = System.Diagnostics.Stopwatch.GetTimestamp();
                            lastWasOneShot = false;
                            didWork = true;
                        }
                    }
                }

                if (!didWork)
                    Thread.Sleep(2);
            }
        }

        /// <summary>
        /// Byte-stuff <paramref name="msg"/> into <paramref name="stuffBuf"/> (growing
        /// it if needed) and write the stuffed bytes to the port in one call.
        /// Returns true on success, false if the port write raised — callers should
        /// still count this as "work done" for loop-pacing decisions.
        /// </summary>
        private bool WriteFrame(byte[] msg, ref byte[] stuffBuf)
        {
            try
            {
                int needed = MozaProtocol.StuffedFrameSize(msg);
                if (stuffBuf.Length < needed)
                    stuffBuf = new byte[Math.Max(needed, stuffBuf.Length * 2)];
                int len = MozaProtocol.StuffFrame(msg, stuffBuf);
                // No lock around the kernel Write syscall. Only this thread
                // calls WriteFrame, and Disconnect/HandleIoFailure close the
                // port without contending. A concurrent Close turns this into
                // an IOException/ObjectDisposedException, which is handled
                // below. Previously we locked around Write; a wedged port
                // (dead tty after sleep/resume) could leave the syscall
                // blocked past WriteTimeout, pinning the lock forever and
                // hanging SimHub shutdown.
                _port?.Write(stuffBuf, 0, len);
                Interlocked.Exchange(ref _consecutiveIoErrors, 0);
                return true;
            }
            catch (Exception ex)
            {
                HandleIoFailure("Write", ex);
                return false;
            }
        }

        /// <summary>
        /// Enumerate every MOZA composite serial port currently visible via WMI,
        /// returning <c>(portName, pid)</c> pairs in WMI iteration order. Empty
        /// list when WMI is unavailable (probe-based discovery handles that path
        /// inside <see cref="FindMozaPort"/>). Public so callers that need to
        /// open more than one MOZA device — e.g. wheelbase + AB9 shifter — can
        /// inspect the full topology before deciding which port belongs to whom.
        /// </summary>
        public static IReadOnlyList<(string PortName, string? Pid)> FindAllMozaPorts()
        {
            var results = new List<(string, string?)>();
            try
            {
                var asm = Assembly.Load("System.Management");
                var searcherType = asm.GetType("System.Management.ManagementObjectSearcher")!;
                using var searcher = (IDisposable)Activator.CreateInstance(
                    searcherType, "SELECT * FROM Win32_PnPEntity WHERE Caption LIKE '%(COM%'")!;
                var collection = (IDisposable)searcherType.GetMethod("Get", Type.EmptyTypes)!.Invoke(searcher, null)!;
                using (collection)
                {
                    foreach (var obj in (IEnumerable)collection)
                    {
                        var objType = obj.GetType();
                        var prop = objType.GetProperty("Item", new[] { typeof(string) })!;
                        var deviceId = prop.GetValue(obj, new object[] { "DeviceID" })?.ToString() ?? "";
                        var caption = prop.GetValue(obj, new object[] { "Caption" })?.ToString() ?? "";

                        if (deviceId.IndexOf("VID_346E", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        int comStart = caption.IndexOf("(COM");
                        if (comStart < 0) continue;
                        int comEnd = caption.IndexOf(")", comStart);
                        if (comEnd <= comStart) continue;

                        var portName = caption.Substring(comStart + 1, comEnd - comStart - 1);
                        results.Add((portName, ExtractPid(deviceId)));
                    }
                }
            }
            catch
            {
                // WMI unavailable (Wine/Proton). Caller falls back to probe path.
            }
            return results;
        }

        private static (string? PortName, string? Pid) FindMozaPort(
            Func<string?, bool>? pidFilter = null,
            Func<bool>? cancel = null)
        {
            // Try WMI-based discovery first (native Windows). Iterate every Moza
            // port and pick the first one that satisfies the optional PID filter
            // — keeps wheelbase code from accidentally opening an attached AB9
            // shifter when both enumerate as VID_346E composite devices.
            var wmiPorts = FindAllMozaPorts();
            if (wmiPorts.Count > 0)
            {
                foreach (var (portName, pid) in wmiPorts)
                {
                    if (pidFilter != null && !pidFilter(pid))
                    {
                        SimHub.Logging.Current.Debug(
                            $"[Moza] Skipping {portName} PID={pid ?? "unknown"} (filtered)");
                        continue;
                    }
                    SimHub.Logging.Current.Info(
                        $"[Moza] Found Moza device on {portName} PID={pid ?? "unknown"} (WMI)");
                    return (portName, pid);
                }
                // No match for this filter — silent at Debug level. Reconnect
                // timer ticks every 5s, so Info would spam the log.
                SimHub.Logging.Current.Debug("[Moza] No matching MOZA device found via WMI");
                return (null, null);
            }

            // WMI unavailable — drop to probe. Probe path can't read PID, so a
            // caller that requires a specific PID (e.g. AB9) must reject null
            // in its filter; the wheelbase default filter accepts null.
            if (pidFilter != null && !pidFilter(null))
            {
                SimHub.Logging.Current.Debug(
                    "[Moza] WMI unavailable and probe path is filtered out; giving up");
                return (null, null);
            }

            SimHub.Logging.Current.Debug("[Moza] WMI discovery returned no devices, trying probe");

            // Probe-based discovery: try opening each COM port and sending a Moza read command.
            // This works under Proton/Wine where COM ports are symlinked to /dev/ttyACM*.
            // We probe high-numbered ports first since Wine maps ttyACM devices to COM33+.
            var ports = SerialPort.GetPortNames();
            Array.Sort(ports, (a, b) =>
            {
                int na = ExtractPortNumber(a);
                int nb = ExtractPortNumber(b);
                return nb.CompareTo(na); // Descending - check high ports first
            });

            // Two-pass probe: bases first, then hubs. v0.7.0 sent both probes per port
            // and returned the first port with any 0x7E reply, which mis-selected the
            // hub when both base + hub were present, or when probe-cycle timing left
            // the base unresponsive after the wrong message hit it.
            //
            // 600ms budget per port — SerialPort.Open can hang indefinitely under Wine
            // if another process holds the tty. Background-thread the probe so one bad
            // port can't block all detection.
            var unreachable = new HashSet<string>();
            foreach (var port in ports)
            {
                if (cancel?.Invoke() == true) return (null, null);

                var (responded, reachable) = ProbeWithTimeout(port, 600, baseProbe: true);
                if (responded)
                {
                    SimHub.Logging.Current.Info($"[Moza] Found Moza base on {port} (probe)");
                    return (port, null);
                }
                if (!reachable) unreachable.Add(port);
            }

            foreach (var port in ports)
            {
                if (cancel?.Invoke() == true) return (null, null);
                if (unreachable.Contains(port)) continue;

                var (responded, _) = ProbeWithTimeout(port, 600, baseProbe: false);
                if (responded)
                {
                    SimHub.Logging.Current.Info($"[Moza] Found Moza hub on {port} (probe)");
                    return (port, null);
                }
            }

            // Drop to Debug — reconnect timer fires every 5s, so Info-level
            // would flood the log when no device is plugged in.
            SimHub.Logging.Current.Debug("[Moza] No MOZA device found on any COM port");
            return (null, null);
        }

        /// <summary>
        /// Extract the PID from a WMI DeviceID string.
        /// Format: USB\VID_346E&amp;PID_XXXX\...
        /// Returns the PID as a hex string like "0x0006", or null if not found.
        /// </summary>
        private static string? ExtractPid(string deviceId)
        {
            int pidIndex = deviceId.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
            if (pidIndex < 0 || pidIndex + 8 > deviceId.Length)
                return null;

            var pidHex = deviceId.Substring(pidIndex + 4, 4);

            // Validate it's actually hex
            foreach (char c in pidHex)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f')))
                    return null;
            }

            return "0x" + pidHex.ToUpperInvariant();
        }

        // Pre-built probe frames. Base targets group 43, device 19, cmd id 2 (state-change probe).
        // Hub targets group 100, device 18, cmd id 3 (port1 power).
        // Base probe arg matches PitHouse wire pattern (documented § Group 0x2B) so device
        // responses stay consistent across clients.
        private static readonly byte[] BaseProbeFrame = BuildProbe(new byte[] { 0x7E, 0x03, 0x2B, 0x13, 0x02, 0x00, 0x00, 0x00 });
        private static readonly byte[] HubProbeFrame  = BuildProbe(new byte[] { 0x7E, 0x03, 0x64, 0x12, 0x03, 0x00, 0x00, 0x00 });

        private static byte[] BuildProbe(byte[] frame)
        {
            // Wire-level checksum so the probe stays valid if the payload ever
            // contains a 0x7E byte (unstuffed wire writes a 0x7E payload byte
            // raw, but the receiver decodes by collapsing 0x7E 0x7E → 0x7E and
            // sums the wire-doubled byte twice). For the current static probes
            // there are no 0x7E bytes from index 2 onward so the result equals
            // CalculateChecksum, but using the wire variant prevents a silent
            // failure if a future probe template is added with a 0x7E in it.
            frame[frame.Length - 1] = MozaProtocol.CalculateWireChecksum(frame, frame.Length - 1);
            return frame;
        }

        /// <summary>
        /// Run ProbeMozaDevice on a background thread with a hard time budget (ms).
        /// Returns (responded, reachable). reachable=false means open/timeout failed
        /// — caller can skip this port in subsequent passes.
        /// </summary>
        private static (bool responded, bool reachable) ProbeWithTimeout(string portName, int timeoutMs, bool baseProbe)
        {
            bool responded = false;
            bool reachable = false;
            var t = new Thread(() =>
            {
                try { (responded, reachable) = ProbeMozaDevice(portName, baseProbe); }
                catch { responded = false; reachable = false; }
            })
            { IsBackground = true, Name = $"MozaProbe-{portName}" };
            t.Start();
            if (!t.Join(timeoutMs))
            {
                SimHub.Logging.Current.Debug($"[Moza] Probe {portName}: timed out after {timeoutMs}ms, skipping");
                return (false, false);
            }
            return (responded, reachable);
        }

        /// <summary>
        /// Open port, send a single base-or-hub probe, wait for any 0x7E reply.
        /// Single-message probe avoids the v0.7.0 issue where back-to-back base+hub
        /// writes left the device in a state where it stopped answering after reopen.
        /// </summary>
        private static (bool responded, bool reachable) ProbeMozaDevice(string portName, bool baseProbe)
        {
            try
            {
                using (var probe = new SerialPort(portName, MozaProtocol.BaudRate)
                {
                    ReadTimeout = 300,
                    WriteTimeout = 300
                })
                {
                    probe.Open();
                    probe.DiscardInBuffer();

                    var msg = baseProbe ? BaseProbeFrame : HubProbeFrame;
                    probe.Write(msg, 0, msg.Length);

                    System.Threading.Thread.Sleep(100);

                    bool responded = false;
                    if (probe.BytesToRead > 0)
                    {
                        int first = probe.ReadByte();
                        responded = first == MozaProtocol.MessageStart;
                    }
                    probe.Close();
                    return (responded, true);
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Debug($"[Moza] Probe {portName}: {ex.GetType().Name}");
                return (false, false);
            }
        }

        private static int ExtractPortNumber(string portName)
        {
            int num = 0;
            for (int i = 0; i < portName.Length; i++)
            {
                if (portName[i] >= '0' && portName[i] <= '9')
                    num = num * 10 + (portName[i] - '0');
            }
            return num;
        }

        public void Dispose()
        {
            _shutdownRequested = true;
            Disconnect();
        }
    }
}
