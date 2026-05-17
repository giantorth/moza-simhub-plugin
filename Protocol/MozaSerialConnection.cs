using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using MozaPlugin.Diagnostics;


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
        // AB9 host-rendered engine vibration: 0x20/dev 0x12/cmd 0x0A 0x05 frames
        // streamed at ~91 Hz from MozaPlugin's worker thread. Latest-wins is
        // correct — only the freshest period needs to reach the wire.
        Ab9EngineVibration = 11,
        // AB9 secondary FFB sub-streams. Each lane is latest-wins because
        // stale frames carry stale phase/magnitude values; the worker
        // recomputes from current RPM at the next tick. Pairs (0x0B 0x02/0x03
        // and 0x08 0x04/0x06) share a lane because they're emitted back-to-back
        // and the device pairs them by tag/phase, not by independent ordering.
        Ab9EnginePulse = 12,
        Ab9TriggerA = 13,        // 0x0D 0x02 + 0x0D 0x03 (flat ~9 Hz keepalive)
        Ab9TriggerRpm = 14,      // 0x0D 0x05 (RPM-tracking trigger)
        Ab9TriggerExtra = 15,    // 0x0D 0x01 (newly-observed sub-cmd)
        Ab9LowRate = 16,         // 0x08 0x04 + 0x08 0x06 (signed-pair low-rate)
    }

    /// <summary>
    /// Which device family the probe path should target when WMI enumeration
    /// fails. Each target uses a frame the device responds to with its own
    /// distinct response group, so a single probe pass can confirm identity
    /// without grabbing the wrong device's port.
    /// </summary>
    public enum MozaProbeTarget
    {
        BaseAndHub,
        Ab9,
    }

    public class MozaSerialConnection : IDisposable
    {
        private const int StreamSlotCount = 17;

        // Ports currently held by a live MozaSerialConnection. Probe path skips
        // these so the AB9 manager (or any future second connection) can't open
        // the wheelbase's tty under Wine — Linux pty serial drivers don't enforce
        // O_EXCL, so a second SerialPort.Open succeeds and any probe written
        // there reaches the device that's already conversing with the first
        // connection. The unsolicited 0x89 reply then lands on the wrong read
        // thread and the parser logs it as a spurious wheel-presence response.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _activePorts =
            new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        // Filter applied during port discovery. Receives the registry-reported
        // PID ("0x1000") or null for probe-based discovery where PID is unknown.
        // Returns true to accept, false to skip. Default = accept all.
        private readonly Func<string?, bool>? _pidFilter;
        private readonly MozaProbeTarget _probeTarget;
        // When set and returning true, FindMozaPort never falls back to the
        // legacy serial-probe path even if the registry returned no MOZA
        // devices. Default (null or false) leaves the fallback armed for the
        // empty-registry case (Wine/Proton without USB enumeration, driver not
        // loaded). Toggled by the user via MozaPluginSettings.DisableSerialProbeFallback.
        private readonly Func<bool>? _disableProbeFallback;

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
        private readonly WriteBudget _budget = new WriteBudget();
        private int _framesDropped;
        private int _checksumFailures;
        private int _frameStartScanResyncs;
        private volatile bool _running;
        private readonly object _lock = new object();
        private string? _lastPortName;

        /// <summary>
        /// Last COM port that connected successfully. Persisted across sessions
        /// by the plugin so <see cref="Connect"/> can try it first on next launch.
        /// </summary>
        public string? LastPortName
        {
            get => _lastPortName;
            set => _lastPortName = value;
        }
        private volatile bool _shutdownRequested;

        // Consecutive I/O error tracking. After sleep/resume the SerialPort handle
        // stays .IsOpen==true but every read/write throws IOException("Not ready"),
        // so nothing triggers reconnect. Count failures and force-close at threshold.
        private int _consecutiveIoErrors;
        // 0 = healthy, 1 = port-dead branch already taken. int+CompareExchange
        // so two failure paths can race the threshold check without both
        // closing the port and double-logging.
        private int _portFailureLogged;
        private const int PortDeadThreshold = 10;

        // Set by ReadLoop to the group byte of the first valid frame
        // received after TryOpen. Used by Connect() to verify a cached
        // port has the expected MOZA device behind it — not a ghost port,
        // not a different device that happens to send bytes. 0 = nothing
        // received yet (0x00 is never a valid response group).
        private int _firstRxGroup;

        // Bitmap of every response group byte ReadLoop has seen since the
        // last Connect() reset. Cached-port validation checks this — looking
        // for the EXPECTED probe response group anywhere in the validation
        // window instead of just the literal first frame. Required because
        // some wheels emit boot-time debug-log frames (group 0x0E) before
        // responding to the base/AB9/hub probe; the probe response then
        // arrives a few ms later mixed in with the noise, and pre-2026-05-10
        // logic at `_firstRxGroup` saw the noise byte first and rejected
        // the (valid) port. 32 bytes × 8 bits covers the full byte range.
        private readonly byte[] _rxGroupsSeen = new byte[32];
        private void NoteRxGroup(byte g) { _rxGroupsSeen[g >> 3] |= (byte)(1 << (g & 7)); }
        private bool HaveSeenRxGroup(byte g) => (_rxGroupsSeen[g >> 3] & (1 << (g & 7))) != 0;
        private void ResetRxGroupsSeen() { for (int i = 0; i < _rxGroupsSeen.Length; i++) _rxGroupsSeen[i] = 0; }

        public event Action<byte[]>? MessageReceived;
        // Raised on the thread that hit the I/O failure (reader or writer)
        // when the port was force-closed by HandleIoFailure. Subscribers
        // must be safe to invoke from a background thread and should not
        // block — used to pause the telemetry sender and reset wheel
        // detection state immediately rather than waiting for the next
        // reconnect-timer tick.
        public event Action? Disconnected;
        public bool IsConnected => _port?.IsOpen == true;

        /// <summary>
        /// Label used when traffic on this connection is recorded by
        /// <see cref="SerialTrafficCapture"/>. Set by the owner (e.g. "wheelbase",
        /// "ab9") so the export can disambiguate frames from each pipe.
        /// </summary>
        public string CaptureLabel { get; set; } = "wheelbase";

        /// <summary>
        /// The HID Product ID discovered from WMI during device enumeration.
        /// Null if PID could not be determined (e.g. probe-based discovery under Wine).
        /// </summary>
        public string? DiscoveredPid { get; private set; }

        /// <summary>
        /// True when the last successful port-detect probe succeeded via the
        /// hub probe (group 0x64, dev 0x12, cmd 0x03). Indicates a Universal Hub
        /// is on the bus. Used by TelemetrySender to gate the post-session-open
        /// 5-slot enumeration burst (`0x64/0x12 cmd 01 NN 00`) that PitHouse
        /// fires only when a hub is attached.
        /// </summary>
        public bool HubProbeSucceeded { get; private set; }

        /// <summary>Sliding-window snapshot of write-budget utilization. Read by
        /// the diagnostics tab so the user can see when the link approaches
        /// saturation. Each call resets the rolling peak.</summary>
        public WriteBudget.Snapshot CurrentBudget => _budget.GetSnapshot();

        /// <summary>Wire-error counters surfaced together — drops on write,
        /// checksum mismatches on read, and frame-start resyncs (junk bytes
        /// skipped between frames).</summary>
        public WireErrorCounters WireErrors => new WireErrorCounters(
            Interlocked.CompareExchange(ref _framesDropped, 0, 0),
            Interlocked.CompareExchange(ref _checksumFailures, 0, 0),
            Interlocked.CompareExchange(ref _frameStartScanResyncs, 0, 0));

        public readonly struct WireErrorCounters
        {
            public readonly int FramesDropped;
            public readonly int ChecksumFailures;
            public readonly int FrameStartScanResyncs;

            public WireErrorCounters(int dropped, int cksum, int resync)
            {
                FramesDropped = dropped;
                ChecksumFailures = cksum;
                FrameStartScanResyncs = resync;
            }
        }

        public MozaSerialConnection() : this(null, MozaProbeTarget.BaseAndHub, null) { }

        /// <summary>
        /// Construct a serial connection scoped to a subset of MOZA composite
        /// devices. <paramref name="pidFilter"/> receives each candidate port's
        /// PID (null only on the legacy probe path where PID is unknown) and
        /// returns true to accept.
        /// <paramref name="probeTarget"/> selects which probe frames are issued
        /// if and only if the legacy probe fallback runs; AB9 callers must pass
        /// <see cref="MozaProbeTarget.Ab9"/>.
        /// <paramref name="disableProbeFallback"/> hard-disables the legacy
        /// probe fallback. Default (null) leaves it armed for the case where
        /// the registry returned no matching MOZA device — then the probe
        /// runs against the unclassified ports as a last resort so users on
        /// systems without USB PnP enumeration (Wine/Proton, broken driver
        /// install) can still connect. The probe never writes bytes at a
        /// port the registry already pinned to a non-matching MOZA PID, and
        /// reduces to a no-op when every COM port is classified — see
        /// <see cref="FindMozaPort"/>.
        /// </summary>
        public MozaSerialConnection(
            Func<string?, bool>? pidFilter,
            MozaProbeTarget probeTarget = MozaProbeTarget.BaseAndHub,
            Func<bool>? disableProbeFallback = null)
        {
            _pidFilter = pidFilter;
            _probeTarget = probeTarget;
            _disableProbeFallback = disableProbeFallback;
        }

        /// <summary>
        /// Format a 16-bit PID as the canonical "0x" + 4-hex-uppercase string
        /// the rest of the plugin (PID filters, DiscoveredPid, device.json
        /// templates) is built around.
        /// </summary>
        private static string FormatPid(ushort pid) =>
            "0x" + pid.ToString("X4", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// Mark the hub as observed on this pipe. Called from MozaPlugin's
        /// device-detect handler when a hub-group response arrives, so the
        /// post-session 5-slot enumeration burst still fires for hub-attached
        /// wheels even though we no longer learn about hubs from a probe.
        /// Idempotent; latched true.
        /// </summary>
        public void MarkHubDetected()
        {
            HubProbeSucceeded = true;
        }

        public bool Connect()
        {
            if (_shutdownRequested)
                return false;

            // Tear down any stale threads/port from a previous dead session
            // (e.g. after sleep/resume killed the tty but handle stayed open).
            if (_running || _port != null)
                Disconnect();

            // Try the last known port first to avoid re-probing — but only if
            // the registry still confirms the port belongs to a MOZA device of
            // the right family. Identity is settled at the registry layer
            // before we open anything, which eliminates two old hazards:
            //  1. The cached port being reassigned to a different MOZA device
            //     after a USB-port swap (e.g. wheelbase relocated to where the
            //     AB9 used to be) — the legacy probe-based liveness check
            //     would mis-validate because AB9's main dev id 0x12 collides
            //     with the wheelbase Hub id and both reply 0x89 to the AB9
            //     identity probe.
            //  2. Ghost registry entries — MozaPortDiscovery filters those out
            //     by cross-referencing against SerialPort.GetPortNames().
            //
            // The `_activePorts` peer-held check stays as defence-in-depth so
            // a same-process sibling connection (wheelbase vs AB9 holding the
            // same saved COM) can't double-open under Wine, which doesn't
            // enforce O_EXCL on pty serial drivers.
            if (_lastPortName != null
                && !_activePorts.ContainsKey(_lastPortName))
            {
                if (MozaPortDiscovery.Instance.TryGetByPort(_lastPortName, out var info)
                    && (_pidFilter == null || _pidFilter(FormatPid(info.Pid))))
                {
                    DiscoveredPid = FormatPid(info.Pid);
                    if (TryOpen(_lastPortName))
                        return true;
                    MozaLog.Debug(
                        $"[Moza] Cached port {_lastPortName} validated but failed to open — clearing");
                    _lastPortName = null;
                }
                else
                {
                    MozaLog.Debug(
                        $"[Moza] Cached port {_lastPortName} no longer matches a MOZA device in the registry — clearing");
                    _lastPortName = null;
                }
            }

            var (portName, pid, viaHubProbe) = FindMozaPort(
                _pidFilter, _probeTarget, _lastPortName, _disableProbeFallback,
                () => _shutdownRequested);
            if (portName == null)
                return false;

            if (pid != null)
                DiscoveredPid = pid;
            HubProbeSucceeded = viaHubProbe;

            return TryOpen(portName);
        }

        private bool TryOpen(string portName)
        {
            // Drain any stale messages from a previous connection
            while (_oneShotQueue.TryDequeue(out _)) { }
            for (int k = 0; k < _streamSlots.Length; k++)
                Interlocked.Exchange(ref _streamSlots[k], null);
            Interlocked.Exchange(ref _firstRxGroup, 0);
            lock (_rxGroupsSeen) ResetRxGroupsSeen();

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
                    // CDC ACM uses DTR as "host connected" signal. Must be true
                    // so Close() drops DTR and Open() raises it — giving the
                    // wheel a real disconnect/reconnect on the control lines.
                    DtrEnable = true,
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
                _activePorts[portName] = 1;
                Interlocked.Exchange(ref _consecutiveIoErrors, 0);
                Interlocked.Exchange(ref _portFailureLogged, 0);
                MozaLog.Info($"[Moza] Connected to {portName}");
                return true;
            }
            catch (Exception ex)
            {
                MozaLog.Error($"[Moza] Failed to connect to {portName}: {ex.Message}");
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
                catch (Exception ex) { MozaLog.Debug($"[Moza] Port close: {ex.Message}"); }
            }

            if (_lastPortName != null)
                _activePorts.TryRemove(_lastPortName, out _);

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
                catch (Exception ex) { MozaLog.Debug($"[Moza] DiscardOutBuffer: {ex.Message}"); }
            }
        }

        // Record an I/O failure. Throttles log spam and force-closes the port
        // once the failure count crosses the threshold so the reconnect timer
        // can reopen it (handles sleep/resume where .IsOpen stays true on dead tty).
        private void HandleIoFailure(string label, Exception ex)
        {
            if (!_running) return;

            int count = Interlocked.Increment(ref _consecutiveIoErrors);

            if (Volatile.Read(ref _portFailureLogged) == 0)
            {
                MozaLog.Error($"[Moza] {label} error: {ex.GetType().Name}: {ex.Message}");
            }

            // Single-winner gate: only one thread crosses threshold AND wins
            // the CAS. Loser path skips the close+log entirely instead of
            // racing on a second close.
            if (count >= PortDeadThreshold &&
                Interlocked.CompareExchange(ref _portFailureLogged, 1, 0) == 0)
            {
                MozaLog.Warn(
                    $"[Moza] Port wedged after {count} consecutive I/O errors — closing for reconnect");
                lock (_lock)
                {
                    try { _port?.Close(); } catch { }
                    _port = null;
                }
                while (_oneShotQueue.TryDequeue(out _)) { }
                for (int k = 0; k < _streamSlots.Length; k++)
                    Interlocked.Exchange(ref _streamSlots[k], null);
                try { Disconnected?.Invoke(); } catch (Exception dex)
                {
                    MozaLog.Debug($"[Moza] Disconnected handler: {dex.Message}");
                }
            }
        }

        private void ReadLoop()
        {
            MozaLog.Debug("[Moza] Read thread started");
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
                        if (frameStart > cursor)
                            Interlocked.Increment(ref _frameStartScanResyncs);
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
                            MozaLog.Debug(
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
                            Interlocked.Increment(ref _checksumFailures);
                            int nn = Math.Min(8, raw.Length);
                            string first8a = nn > 0 ? BitConverter.ToString(raw, 0, nn) : "(empty)";
                            MozaLog.Debug(
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
                            MozaLog.Debug(
                                $"[Moza] Received msg #{messageCount}: len={payloadLength} " +
                                $"group=0x{data[0]:X2} dev=0x{data[1]:X2} ({data.Length} bytes)");
                        }
                        // Diagnostic: per-chunk log for SerialStream session-data
                        // frames (0xC3 / wheel / 7C / 00) — session 0x09 chunk reception.
                        if (data.Length >= 8 && data[0] == MozaProtocol.SerialStreamRespGroup
                            && data[1] == MozaProtocol.WheelDeviceIdSwapped
                            && data[2] == MozaProtocol.SerialStreamOpcodeData
                            && data[3] == 0x00)
                        {
                            byte sess = data[4];
                            byte type = data[5];
                            int seqWire = data[6] | (data[7] << 8);
                            int bodyLen = data.Length - 8;
                            string first8 = bodyLen > 0
                                ? BitConverter.ToString(data, 8, Math.Min(8, bodyLen))
                                : "(empty)";
                            MozaLog.Debug(
                                $"[Moza] WIRE sess=0x{sess:X2} type=0x{type:X2} seq={seqWire} " +
                                $"totalLen={data.Length} payload={bodyLen}B first8={first8}");
                        }
                        Interlocked.CompareExchange(ref _firstRxGroup, data[0], 0);
                        // Also record every group seen so cached-port
                        // validation can find the expected response group
                        // even when boot-time debug spam beats it to the port.
                        lock (_rxGroupsSeen) NoteRxGroup(data[0]);
                        SerialTrafficCapture.Instance.RecordRx(CaptureLabel, data);
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
            MozaLog.Debug("[Moza] Write thread started");
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
            long oneSecTicks = stopwatchFreq;
            long lastWriteTs = System.Diagnostics.Stopwatch.GetTimestamp() - stopwatchFreq;
            long lastBudgetWarnTs = 0;
            bool lastWasOneShot = false;

            while (_running)
            {
                bool didWork = false;

                // 1) One-shot FIFO: session traffic, settings writes, probes.
                //    Paced at 4 ms between consecutive one-shots — Moza bases drop
                //    settings writes when flooded (ApplyProfile sends 30+ in a burst).
                //    Pacing is skipped when the previous write was a stream frame,
                //    since telemetry-group writes never trigger the drop.
                //
                //    Under bandwidth pressure, the budget extends the gate further
                //    (still order-preserving FIFO, never drops) so the wheel can
                //    drain its receive buffer before we pile on more.
                if (_oneShotQueue.TryDequeue(out var msg))
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    int stuffedSize = MozaProtocol.StuffedFrameSize(msg);
                    int budgetExtraMs = _budget.RecommendOneShotDelayMs(stuffedSize);
                    int baseGapMs = 0;
                    if (lastWasOneShot)
                    {
                        long sinceTicks = now - lastWriteTs;
                        if (sinceTicks < fourMsTicks)
                            baseGapMs = (int)((fourMsTicks - sinceTicks) * 1000 / stopwatchFreq);
                    }
                    int sleepMs = Math.Max(baseGapMs, budgetExtraMs);
                    if (sleepMs > 0) Thread.Sleep(sleepMs);

                    int written = WriteFrame(msg, ref stuffBuf, stuffedSize);
                    if (written > 0)
                    {
                        writeCount++;
                        if (writeCount <= 5)
                            MozaLog.Debug($"[Moza] Sent cmd #{writeCount}: {msg.Length} bytes, group=0x{(msg.Length > 2 ? msg[2] : 0):X2}");
                        lastWriteTs = System.Diagnostics.Stopwatch.GetTimestamp();
                        lastWasOneShot = true;
                    }
                    didWork = true;
                }

                // 2) Stream lane: latest-wins per kind, drained after every FIFO
                //    item (not only when FIFO is empty). Retransmit + blind
                //    retransmit can keep the FIFO perpetually non-empty for seconds,
                //    starving value frames if streams are gated on FIFO-empty.
                //
                //    The stream lane is NOT bandwidth-gated. Latest-wins
                //    coalescing already provides natural backpressure — when
                //    writes lag, fresh SendStream calls overwrite the slot
                //    before the previous frame is dequeued, so the wheel sees
                //    the freshest value automatically. SerialPort.Write blocks
                //    when the OS buffer fills (16 kB), giving us a hard
                //    physical-layer gate without artificial software gating.
                //    Software gating here was the 2026-05-08 regression: it
                //    held value frames for ~700ms-1s during routine bursts and
                //    starved the wheel into dropping configJson chunks during
                //    cold-start and post-switch.
                for (int k = 0; k < _streamSlots.Length; k++)
                {
                    var slot = Interlocked.Exchange(ref _streamSlots[k], null);
                    if (slot == null) continue;
                    int written = WriteFrame(slot, ref stuffBuf, MozaProtocol.StuffedFrameSize(slot));
                    if (written > 0)
                    {
                        writeCount++;
                        lastWriteTs = System.Diagnostics.Stopwatch.GetTimestamp();
                        lastWasOneShot = false;
                        didWork = true;
                    }
                }

                // Periodic budget warning — single line per second, only when over
                // 90 % of target. Lets the user / log analyzer correlate stress
                // periods with downstream symptoms (frame drops, retransmits).
                long warnNow = System.Diagnostics.Stopwatch.GetTimestamp();
                if (warnNow - lastBudgetWarnTs >= oneSecTicks)
                {
                    // PeekSnapshot doesn't reset the rolling peak — leaves it
                    // intact for the diagnostics tab's GetSnapshot to consume.
                    var snap = _budget.PeekSnapshot();
                    if (snap.PercentBudget >= 90)
                    {
                        MozaLog.Warn(
                            $"[Moza] Write budget {snap.PercentBudget}% ({snap.BytesLastSec} B/s, peak {snap.PeakBurstBytes})");
                    }
                    lastBudgetWarnTs = warnNow;
                }

                if (!didWork)
                    Thread.Sleep(2);
            }
        }

        /// <summary>
        /// Byte-stuff <paramref name="msg"/> into <paramref name="stuffBuf"/> (growing
        /// it if needed) and write the stuffed bytes to the port in one call.
        /// Returns the wire-byte count on success or -1 if the port write raised.
        /// Callers pass <paramref name="needed"/> (precomputed via
        /// <see cref="MozaProtocol.StuffedFrameSize"/>) so this method doesn't
        /// re-scan the input twice on the one-shot hot path.
        /// </summary>
        private int WriteFrame(byte[] msg, ref byte[] stuffBuf, int needed)
        {
            try
            {
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
                SerialTrafficCapture.Instance.RecordTx(CaptureLabel, msg);
                _budget.Record(len);
                return len;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _framesDropped);
                HandleIoFailure("Write", ex);
                return -1;
            }
        }

        /// <summary>
        /// Locate a MOZA device the connection should attach to. Two-stage chain:
        ///   1. <see cref="MozaPortDiscovery"/> registry walk (the always-on
        ///      primary path on Windows, and on Wine/Proton when the Wine
        ///      USB subsystem populates the registry tree). Returns directly
        ///      if any port satisfies the PID filter — no serial bytes
        ///      written, identity settled at the registry layer.
        ///   2. Legacy serial-probe fallback. Runs when the registry didn't
        ///      give us a direct match. Honours the registry classifications
        ///      it does have: ports the registry already pinned to a PID
        ///      matching <paramref name="pidFilter"/> are claimed
        ///      immediately without serial bytes; ports pinned to a
        ///      mismatching MOZA PID are skipped (so we never re-send a
        ///      base/hub/AB9 probe at hardware we've already identified —
        ///      this also preserves the original "no AB9 probe storm at a
        ///      Windows wheelbase-only user" behaviour because every COM
        ///      port ends up classified). Only unclassified ports actually
        ///      receive probe writes. <paramref name="disableProbeFallback"/>
        ///      hard-disables the probe (user opt-out via
        ///      <c>DisableSerialProbeFallback</c>).
        ///
        /// <paramref name="preferredPort"/> tilts the registry result selection
        /// toward the saved port when multiple MOZA devices of the same family
        /// are present (multi-rig setups, or transient duplicate enumerations).
        /// </summary>
        private static (string? PortName, string? Pid, bool ViaHubProbe) FindMozaPort(
            Func<string?, bool>? pidFilter,
            MozaProbeTarget probeTarget,
            string? preferredPort,
            Func<bool>? disableProbeFallback,
            Func<bool>? cancel = null)
        {
            // Stage 1: registry walk. Take the full MOZA enumeration first
            // so we can distinguish "the registry is working but our PID
            // isn't there" from "the registry sees nothing at all".
            var allRegistryPorts = MozaPortDiscovery.Instance.Enumerate();

            // Per-port lookup we'll reuse in the probe loops below so the
            // probe never writes bytes at a port whose PID is already
            // known to belong to a different device category.
            var registryByPort = new Dictionary<string, MozaPortDiscovery.PortInfo>(
                allRegistryPorts.Count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < allRegistryPorts.Count; i++)
                registryByPort[allRegistryPorts[i].PortName] = allRegistryPorts[i];

            // Filter through the existing string-based pidFilter contract.
            var matchingPorts = pidFilter == null
                ? allRegistryPorts
                : (IReadOnlyList<MozaPortDiscovery.PortInfo>)allRegistryPorts
                    .Where(p => pidFilter(FormatPid(p.Pid))).ToList();

            if (matchingPorts.Count > 0)
            {
                MozaPortDiscovery.PortInfo chosen = matchingPorts[0];
                if (!string.IsNullOrEmpty(preferredPort))
                {
                    for (int i = 0; i < matchingPorts.Count; i++)
                    {
                        if (string.Equals(matchingPorts[i].PortName, preferredPort,
                                          StringComparison.OrdinalIgnoreCase))
                        {
                            chosen = matchingPorts[i];
                            break;
                        }
                    }
                }
                MozaLog.Debug(
                    $"[Moza] Found MOZA device on {chosen.PortName} PID={FormatPid(chosen.Pid)} (registry)");
                return (chosen.PortName, FormatPid(chosen.Pid), false);
            }

            if (disableProbeFallback?.Invoke() == true)
            {
                MozaLog.Debug(
                    "[Moza] No matching MOZA device in registry; DisableSerialProbeFallback is on so probe is skipped");
                return (null, null, false);
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

            // Count probe-eligible ports — anything the registry hasn't already
            // pinned to a non-matching PID. If the registry covers every COM
            // port with non-matching classifications, there's nothing left to
            // probe, so we skip the probe entirely and preserve the historical
            // "trust the registry's no answer" behaviour (this is what kept
            // the AB9 probe storm off Windows users with a wheelbase but no
            // AB9). When at least one unclassified-or-matching port exists,
            // we let the probe run — the per-port guard inside each loop
            // skips registry-classified non-matching ports one by one.
            int probeEligible = 0;
            for (int i = 0; i < ports.Length; i++)
            {
                if (!registryByPort.TryGetValue(ports[i], out var info))
                {
                    probeEligible++;
                    continue;
                }
                if (pidFilter == null || pidFilter(FormatPid(info.Pid)))
                    probeEligible++;
            }
            if (probeEligible == 0 && allRegistryPorts.Count > 0)
            {
                MozaLog.Debug(
                    $"[Moza] Registry classifies all {ports.Length} COM port(s); none match this connection's PID filter — skipping probe (trust registry)");
                return (null, null, false);
            }

            if (allRegistryPorts.Count == 0)
                MozaLog.Debug("[Moza] No MOZA device in registry, falling back to serial probe");
            else
                MozaLog.Debug(
                    $"[Moza] Registry classifies {registryByPort.Count} of {ports.Length} COM port(s); probing the remainder");

            // 600ms budget per port — SerialPort.Open can hang indefinitely under Wine
            // if another process holds the tty. Background-thread the probe so one bad
            // port can't block all detection.
            var unreachable = new HashSet<string>();

            // Skip ports already held by a sibling MozaSerialConnection. Linux
            // pty drivers don't enforce O_EXCL so a probe Open() on the live
            // wheelbase tty would succeed and the probe write would reach the
            // device that connection is already conversing with — the resulting
            // 0x89 reply lands on the original read thread and the parser logs
            // a spurious wheel-presence response every probe cycle.
            bool IsHeldByPeer(string port) => _activePorts.ContainsKey(port);

            // Registry-aware guard for a single port. Three outcomes:
            //   * unclassified  → return false, caller probes the port.
            //   * matching PID  → write `decided` with a no-probe direct
            //                     claim using the registry-reported PID.
            //   * mismatching   → write `decided` = (null, null, false)
            //                     and caller `continue`s past this port.
            // Centralised so AB9 and BaseAndHub branches share identical
            // behaviour and log lines.
            bool RegistrySaysSkip(string port, out (string?, string?, bool) decided)
            {
                decided = (null, null, false);
                if (!registryByPort.TryGetValue(port, out var info)) return false;
                string pidStr = FormatPid(info.Pid);
                if (pidFilter == null || pidFilter(pidStr))
                {
                    MozaLog.Debug(
                        $"[Moza] Port {port} already classified by registry as PID={pidStr} ({MozaUsbIds.Describe(info.Pid)}) — claiming without probe");
                    decided = (port, pidStr, false);
                    return true;
                }
                MozaLog.Debug(
                    $"[Moza] Port {port} classified by registry as PID={pidStr} ({MozaUsbIds.Describe(info.Pid)}) — not for this connection, skipping probe");
                // Sentinel: mismatching classification — caller treats as
                // "skip and continue" by checking decided.Item1 == null
                // && we returned true.
                return true;
            }

            if (probeTarget == MozaProbeTarget.Ab9)
            {
                // AB9 main device id (0x12) is shared with the wheelbase Main
                // controller, and the wheelbase happily answers AB9 identity
                // probes — so claiming a port purely on a 0x89 reply mis-claims
                // a wheelbase tty whenever the wheelbase manager hasn't locked
                // its port yet (e.g. first reconnect tick after plugin start).
                // Disambiguate by running a base probe (group 0x2B dev 0x13)
                // first: AB9 has no dev 0x13 and never replies to it, while
                // the wheelbase always does. A 0xAB reply means "this is a
                // wheelbase" → skip. Only ports that don't respond to the base
                // probe get the AB9 probe.
                //
                // The base-disambig probe is suppressed entirely for ports
                // the registry already pinned to a wheelbase PID — that's
                // exactly the leak the per-port registry guard fixes.
                foreach (var port in ports)
                {
                    if (cancel?.Invoke() == true) return (null, null, false);
                    if (IsHeldByPeer(port)) continue;
                    if (RegistrySaysSkip(port, out var decided))
                    {
                        if (decided.Item1 != null) return decided;
                        continue;
                    }

                    var (baseResp, baseReach) = ProbeWithTimeout(port, 600, ProbeKind.Base);
                    if (!baseReach) { unreachable.Add(port); continue; }
                    if (baseResp)
                    {
                        MozaLog.Debug($"[Moza] Probe {port} Ab9: base probe matched — wheelbase territory, skipping");
                        continue;
                    }

                    var (ab9Resp, _) = ProbeWithTimeout(port, 600, ProbeKind.Ab9);
                    if (ab9Resp)
                    {
                        MozaLog.Info($"[Moza] Found Moza AB9 shifter on {port} (probe)");
                        return (port, null, false);
                    }
                }

                MozaLog.Debug("[Moza] No AB9 device found on any COM port");
                return (null, null, false);
            }

            // BaseAndHub: two-pass probe — bases first, then hubs. v0.7.0 sent both
            // probes per port and returned the first port with any 0x7E reply, which
            // mis-selected the hub when both base + hub were present, or when probe-
            // cycle timing left the base unresponsive after the wrong message hit it.
            foreach (var port in ports)
            {
                if (cancel?.Invoke() == true) return (null, null, false);
                if (IsHeldByPeer(port)) continue;
                if (RegistrySaysSkip(port, out var decided))
                {
                    if (decided.Item1 != null) return decided;
                    continue;
                }

                var (responded, reachable) = ProbeWithTimeout(port, 600, ProbeKind.Base);
                if (responded)
                {
                    MozaLog.Info($"[Moza] Found Moza base on {port} (probe)");
                    return (port, null, false);
                }
                if (!reachable) unreachable.Add(port);
            }

            foreach (var port in ports)
            {
                if (cancel?.Invoke() == true) return (null, null, false);
                if (unreachable.Contains(port)) continue;
                if (IsHeldByPeer(port)) continue;
                // Pass 1 already short-circuited on registry-matching ports
                // via RegistrySaysSkip, so a registry-classified port reaching
                // here is guaranteed mismatching — skip without re-logging.
                if (registryByPort.ContainsKey(port)) continue;

                var (responded, _) = ProbeWithTimeout(port, 600, ProbeKind.Hub);
                if (responded)
                {
                    MozaLog.Info($"[Moza] Found Moza hub on {port} (probe)");
                    return (port, null, true);
                }
            }

            // Drop to Debug — reconnect timer fires every 5s, so Info-level
            // would flood the log when no device is plugged in.
            MozaLog.Debug("[Moza] No MOZA device found on any COM port");
            return (null, null, false);
        }

        private enum ProbeKind { Base, Hub, Ab9 }

        // Pre-built probe frames. Base targets group 43, device 19, cmd id 2 (state-change probe).
        // Hub targets group 100, device 18, cmd id 3 (port1 power).
        // AB9 targets group 9, device 18 — PitHouse identity probe; the AB9 main
        // device shares dev id 0x12 with Hub but does not respond to the hub-read
        // group 0x64, so its response group (0x89) cleanly disambiguates the two.
        // Base probe arg matches PitHouse wire pattern (documented § Group 0x2B) so device
        // responses stay consistent across clients.
        private static readonly byte[] BaseProbeFrame = BuildProbe(new byte[] { 0x7E, 0x03, 0x2B, 0x13, 0x02, 0x00, 0x00, 0x00 });
        private static readonly byte[] HubProbeFrame  = BuildProbe(new byte[] { 0x7E, 0x03, 0x64, 0x12, 0x03, 0x00, 0x00, 0x00 });
        private static readonly byte[] Ab9ProbeFrame  = BuildProbe(new byte[] { 0x7E, 0x00, 0x09, 0x12, 0x00 });

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
        /// Run the probe on a background thread with a hard time budget (ms).
        /// Returns (responded, reachable). reachable=false means open/timeout failed
        /// — caller can skip this port in subsequent passes.
        ///
        /// On timeout, the outer thread closes the SerialPort to unblock any inner
        /// syscall (Open/Write/Read) — without this, a stuck Write would leave the
        /// probe thread alive holding the port handle until the OS eventually
        /// returned, leaking threads under repeat probe pressure.
        /// </summary>
        private static (bool responded, bool reachable) ProbeWithTimeout(string portName, int timeoutMs, ProbeKind kind)
        {
            bool responded = false;
            bool reachable = false;
            SerialPort? portRef = null;

            var t = new Thread(() =>
            {
                SerialPort? probe = null;
                try
                {
                    probe = new SerialPort(portName, MozaProtocol.BaudRate)
                    {
                        ReadTimeout = 300,
                        WriteTimeout = 300
                    };
                    // Publish BEFORE Open() so a timed-out caller can close the
                    // port even if Open itself blocks on a syscall.
                    System.Threading.Volatile.Write(ref portRef, probe);
                    probe.Open();
                    (responded, reachable) = ProbeMozaDeviceCore(probe, kind, portName);
                }
                catch { responded = false; reachable = false; }
                finally
                {
                    try { probe?.Close(); } catch { }
                    try { probe?.Dispose(); } catch { }
                }
            })
            { IsBackground = true, Name = $"MozaProbe-{portName}" };
            t.Start();

            if (!t.Join(timeoutMs))
            {
                // Force the inner syscall to throw by closing the port from
                // this side. .NET SerialPort.Close from another thread while
                // a Write is in progress is documented to throw IOException
                // on the blocked call — which the inner catch handles.
                var p = System.Threading.Volatile.Read(ref portRef);
                try { p?.Close(); } catch { }
                try { p?.Dispose(); } catch { }
                try { t.Join(500); } catch { }
                MozaLog.Debug($"[Moza] Probe {portName}: timed out after {timeoutMs}ms (port force-closed)");
                return (false, false);
            }
            return (responded, reachable);
        }

        /// <summary>
        /// Send the requested probe on an already-open port and confirm the reply
        /// by checking the response group at wire offset 2 against the expected
        /// toggled-bit-7 group for the kind being probed. Disambiguating by response
        /// group stops AB9 (dev id 0x12, same as Hub) from being misidentified as a
        /// hub when both share the bus.
        /// Single-message probe avoids the v0.7.0 issue where back-to-back base+hub
        /// writes left the device in a state where it stopped answering after reopen.
        ///
        /// Caller (ProbeWithTimeout) owns the port lifecycle so a hung syscall can
        /// be unblocked by closing the port from outside.
        /// </summary>
        private static (bool responded, bool reachable) ProbeMozaDeviceCore(SerialPort probe, ProbeKind kind, string portName)
        {
            byte[] msg;
            byte expectedRespGroup;
            switch (kind)
            {
                case ProbeKind.Base: msg = BaseProbeFrame; expectedRespGroup = MozaProtocol.BaseRespGroup; break;
                case ProbeKind.Hub:  msg = HubProbeFrame;  expectedRespGroup = MozaProtocol.HubRespGroup;  break;
                case ProbeKind.Ab9:  msg = Ab9ProbeFrame;  expectedRespGroup = MozaProtocol.Ab9RespGroup;  break;
                default: return (false, false);
            }

            try
            {
                probe.DiscardInBuffer();

                // Generous validation window with periodic re-probes. The
                // wheel commonly emits a burst of boot-time debug-log
                // frames (group 0x0E, ASCII text) for several hundred ms
                // on first open, drowning out a single 100ms probe-and-
                // peek. We poll in short slices, re-emit the probe on a
                // ~200ms cadence, and accept as soon as we see ANY frame
                // whose group at wire offset +2 matches the expected
                // response — surviving leading debug spam without a
                // false positive on 0x0E frames.
                const int TotalBudgetMs = 500;
                const int ProbeRepeatMs = 200;
                const int PollSliceMs = 25;
                const int MaxAccumBytes = 4096;

                var accum = new System.Collections.Generic.List<byte>(512);
                byte firstSeenGroup = 0xFF;
                bool responded = false;

                int waited = 0;
                int nextProbeAt = 0;
                while (waited < TotalBudgetMs)
                {
                    if (waited >= nextProbeAt)
                    {
                        try { probe.Write(msg, 0, msg.Length); } catch { return (false, false); }
                        nextProbeAt = waited + ProbeRepeatMs;
                    }

                    System.Threading.Thread.Sleep(PollSliceMs);
                    waited += PollSliceMs;

                    int avail = probe.BytesToRead;
                    if (avail > 0)
                    {
                        int want = Math.Min(avail, MaxAccumBytes - accum.Count);
                        if (want > 0)
                        {
                            var tmp = new byte[want];
                            int n = probe.Read(tmp, 0, want);
                            for (int i = 0; i < n; i++) accum.Add(tmp[i]);
                        }
                        for (int i = 0; i + 2 < accum.Count; i++)
                        {
                            if (accum[i] != MozaProtocol.MessageStart) continue;
                            byte respGroup = accum[i + 2];
                            if (firstSeenGroup == 0xFF) firstSeenGroup = respGroup;
                            if (respGroup == expectedRespGroup)
                            {
                                responded = true;
                                break;
                            }
                        }
                        if (responded) break;
                    }
                }

                if (!responded && firstSeenGroup != 0xFF)
                {
                    MozaLog.Debug(
                        $"[Moza] Probe {portName} {kind}: {accum.Count}B in {waited}ms, " +
                        $"no 0x{expectedRespGroup:X2} (first seen 0x{firstSeenGroup:X2})");
                }
                return (responded, true);
            }
            catch (Exception ex)
            {
                MozaLog.Debug($"[Moza] Probe {portName}: {ex.GetType().Name}");
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
