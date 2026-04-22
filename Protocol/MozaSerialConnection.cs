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
    public class MozaSerialConnection : IDisposable
    {
        private volatile SerialPort? _port;
        private Thread? _readThread;
        private Thread? _writeThread;
        private readonly ConcurrentQueue<byte[]> _writeQueue = new ConcurrentQueue<byte[]>();
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

            var (portName, pid) = FindMozaPort(() => _shutdownRequested);
            if (portName == null)
                return false;

            if (pid != null)
                DiscoveredPid = pid;

            return TryOpen(portName);
        }

        private bool TryOpen(string portName)
        {
            // Drain any stale messages from a previous connection
            while (_writeQueue.TryDequeue(out _)) { }

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
            _readThread?.Join(1000);
            _writeThread?.Join(1000);

            lock (_lock)
            {
                if (_port?.IsOpen == true)
                {
                    try { _port.Close(); }
                    catch (Exception ex) { SimHub.Logging.Current.Debug($"[Moza] Port close: {ex.Message}"); }
                }
                _port = null;
            }
        }

        public void Send(byte[] message)
        {
            if (message != null)
                _writeQueue.Enqueue(message);
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
                while (_writeQueue.TryDequeue(out _)) { }
            }
        }

        private void ReadLoop()
        {
            SimHub.Logging.Current.Info("[Moza] Read thread started");
            int messageCount = 0;
            // Bulk read buffer — drains all available bytes from the OS read
            // buffer in one SerialPort.Read() call, then parses frames from
            // this byte array in memory. Per-byte ReadByte() under Wine/tty0tty
            // had ~100μs per-call overhead, causing plugin to fall behind sim's
            // bursted session 0x09 state pushes and silently lose chunks 1-6
            // of 7 (seen 2026-04-22). Bulk read eliminates the overhead.
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

            while (_running)
            {
                if (_writeQueue.TryDequeue(out var msg))
                {
                    try
                    {
                        lock (_lock)
                        {
                            // Moza byte-stuffing: header (start + len) goes raw; every
                            // 0x7E byte after that is doubled on the wire so the
                            // receiver doesn't treat it as a new frame start. Applies
                            // to group, device, payload, and checksum alike.
                            if (msg.Length >= 2)
                                _port?.Write(msg, 0, 2);
                            byte stuff = MozaProtocol.MessageStart;
                            for (int i = 2; i < msg.Length; i++)
                            {
                                _port?.Write(msg, i, 1);
                                if (msg[i] == MozaProtocol.MessageStart)
                                    _port?.Write(new byte[] { stuff }, 0, 1);
                            }
                        }
                        writeCount++;
                        if (writeCount <= 5)
                            SimHub.Logging.Current.Info($"[Moza] Sent cmd #{writeCount}: {msg.Length} bytes, group=0x{(msg.Length > 2 ? msg[2] : 0):X2}");

                        Interlocked.Exchange(ref _consecutiveIoErrors, 0);

                        // Pace writes: Moza bases drop commands when flooded with rapid-fire
                        // settings writes (e.g. ApplyProfile sends 30+ commands in a burst).
                        // 4ms matches boxflat's proven timing for reliable device writes.
                        if (!_writeQueue.IsEmpty)
                            Thread.Sleep(4);
                    }
                    catch (Exception ex)
                    {
                        HandleIoFailure("Write", ex);
                    }
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
        }

        private static (string? PortName, string? Pid) FindMozaPort(Func<bool>? cancel = null)
        {
            // Try WMI-based discovery first (native Windows), loaded optionally via reflection
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

                        if (deviceId.IndexOf("VID_346E", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            int comStart = caption.IndexOf("(COM");
                            if (comStart >= 0)
                            {
                                int comEnd = caption.IndexOf(")", comStart);
                                if (comEnd > comStart)
                                {
                                    var portName = caption.Substring(comStart + 1, comEnd - comStart - 1);
                                    var pid = ExtractPid(deviceId);
                                    SimHub.Logging.Current.Info($"[Moza] Found Moza device on {portName} PID={pid ?? "unknown"} (WMI)");
                                    return (portName, pid);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Info($"[Moza] WMI discovery unavailable ({ex.GetType().Name}), trying probe");
            }

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

            SimHub.Logging.Current.Info("[Moza] No MOZA device found on any COM port");
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
            frame[frame.Length - 1] = MozaProtocol.CalculateChecksum(frame, frame.Length - 1);
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
