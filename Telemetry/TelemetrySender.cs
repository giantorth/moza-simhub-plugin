using System;
using System.Threading;
using System.Timers;
using GameReaderCommon;
using MozaPlugin.Protocol;
using Timer = System.Timers.Timer;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Periodically encodes game data and sends telemetry frames to the wheel.
    ///
    /// Startup follows Pithouse's observed sequence (from USB capture analysis):
    ///   1. Probe for available SerialStream ports (try type=0x81, wait for fc:00 ack)
    ///   2. Open management session + telemetry session on consecutive ports
    ///   3. Ack incoming channel data on telemetry session with fc:00 (~1 second)
    ///   4. Send 0x40 channel config burst (1E enables, 09:00, 28:02)
    ///   5. Begin 0x41 enable signal + 7d:23 telemetry with flag=FlagByte
    ///
    /// Port allocation: the wheel uses a global monotonic counter shared between
    /// host and device. We probe from port 1 upward, sending a type=0x81 session
    /// open and waiting ~50ms for an fc:00 ack. The first port that gets acked
    /// becomes the management session; the next acked port becomes the telemetry
    /// session and its port number is used as the FlagByte for all 7d:23 frames.
    ///
    /// Each tier in the MultiStreamProfile runs at its own rate derived from package_level.
    /// Flag bytes are FlagByte + tier index (sorted by package_level ascending).
    /// </summary>
    public class TelemetrySender : IDisposable
    {
        private readonly MozaSerialConnection _connection;
        private Timer? _sendTimer;
        private TierState[]? _tiers;
        private volatile StatusDataBase? _latestGameData;
        private volatile bool _enabled;
        private int _tickCounter;
        private int _testFrameCounter;
        private int _modeCounter;
        private int _slowCounter;
        private int _baseTickMs;  // Timer period derived from fastest tier's package_level
        private byte _sequenceCounter;
        private int _displayConfigPage;

        // Preamble state
        private bool _preambleComplete;
        private int _preambleTickTarget;
        private int _sessionAckSeq;

        // Port probing state
        private volatile byte _lastAckedSession;  // Set by OnMessageDuringPreamble when fc:00 arrives
        private readonly ManualResetEventSlim _ackReceived = new ManualResetEventSlim(false);

        // Display sub-device detection
        private volatile bool _displayDetected;
        private string _displayModelName = "";

        /// <summary>
        /// True if the wheel's internal Display sub-device responded to identity probe.
        /// Use this to gate dashboard telemetry features in the UI — wheels without
        /// a display (e.g. CS V2.1 with RPM LEDs only) won't have this set.
        /// </summary>
        public bool DisplayDetected => _displayDetected;

        /// <summary>Display sub-device model name, e.g. "Display". Empty if not detected.</summary>
        public string DisplayModelName => _displayModelName;

        // Pre-cached frames (built once, reused every tick)
        private byte[] _cachedEnableFrame = null!;
        private byte[] _cachedModeFrame = null!;
        private byte[] _cachedSequenceFrame = null!;
        private byte[][] _cachedHeartbeatFrames = null!;

        // The telemetry session port / flag byte. Determined during port probing.
        public byte FlagByte { get; set; } = 0x02;
        public bool SendTelemetryMode { get; set; } = true;
        public bool SendSequenceCounter { get; set; } = true;
        public bool TestMode { get; set; } = false;

        /// <summary>Maximum port number to try during probing before giving up.</summary>
        private const byte MaxProbePort = 0x30;

        /// <summary>How long to wait for an fc:00 ack per probe attempt.</summary>
        private const int ProbeTimeoutMs = 80;

        public MultiStreamProfile? Profile
        {
            get => _profile;
            set
            {
                _profile = value;
                if (value == null || value.Tiers.Count == 0)
                {
                    _tiers = null;
                    _baseTickMs = 33;
                    return;
                }

                _baseTickMs = value.Tiers[0].PackageLevel;

                _tiers = new TierState[value.Tiers.Count];
                for (int i = 0; i < value.Tiers.Count; i++)
                {
                    var tier = value.Tiers[i];
                    int tickInterval = Math.Max(1, tier.PackageLevel / _baseTickMs);
                    _tiers[i] = new TierState
                    {
                        Builder = new TelemetryFrameBuilder(tier),
                        TickInterval = tickInterval,
                    };
                }
            }
        }
        private MultiStreamProfile? _profile;

        private volatile int _framesSent;
        public int FramesSent => _framesSent;
        public byte[]? LastFrameSent { get; private set; }
        public TelemetryDiagnostics Diagnostics { get; } = new TelemetryDiagnostics();

        public TelemetrySender(MozaSerialConnection connection)
        {
            _connection = connection;
        }

        public void Start()
        {
            Stop();
            _enabled = true;
            _tickCounter = 0;
            _modeCounter = 0;
            _testFrameCounter = 0;
            _framesSent = 0;
            _sequenceCounter = 0;
            _slowCounter = 0;
            _displayConfigPage = 0;
            _preambleComplete = false;
            _sessionAckSeq = 0;
            _preambleTickTarget = Math.Max(1, 1000 / _baseTickMs);

            BuildCachedFrames();

            // Subscribe early so we catch fc:00 acks during port probing AND preamble
            _connection.MessageReceived += OnMessageDuringPreamble;

            // Probe for available ports and open sessions.
            // This may run on a background thread (dispatched by StartTelemetryIfReady)
            // so the serial read thread stays free to deliver fc:00 ack responses.
            ProbeAndOpenSessions();

            // Bail out if Stop() was called while we were probing
            if (!_enabled) return;

            // Send the tier definition message on the telemetry session.
            // This tells the wheel how to decode each flag byte's bit-packed data:
            // channel indices, compression codes, and bit widths per tier.
            // Without this, the wheel cannot interpret 7d:23 telemetry frames.
            SendTierDefinition();

            // Probe the Display sub-device inside the wheel.
            // Pithouse sends this at t=9.97 (after telemetry starts at t=9.88).
            // The response tells us if the wheel has a built-in display.
            // Non-blocking: responses are caught by OnMessageDuringPreamble.
            SendDisplayProbe();

            // Final check before creating the timer — if Stop() was called during
            // tier definition or display probe, don't create an orphaned timer.
            if (!_enabled) return;

            double intervalMs = _baseTickMs;
            _sendTimer = new Timer(intervalMs) { AutoReset = true };
            _sendTimer.Elapsed += OnTimerElapsed;
            _sendTimer.Start();
        }

        public void Stop()
        {
            _enabled = false;
            _connection.MessageReceived -= OnMessageDuringPreamble;
            if (_sendTimer != null)
            {
                _sendTimer.Stop();
                _sendTimer.Elapsed -= OnTimerElapsed;
                _sendTimer.Dispose();
                _sendTimer = null;
            }
        }

        public void UpdateGameData(StatusDataBase? data)
        {
            _latestGameData = data;
        }

        // ── Port probing ────────────────────────────────────────────────────

        /// <summary>
        /// Probe for available SerialStream ports by sending type=0x81 session opens
        /// and waiting for fc:00 acks. The wheel uses a global monotonic port counter;
        /// ports already consumed by Pithouse won't ack. We need two consecutive ports:
        /// one for management (like Pithouse session 0x01) and one for telemetry
        /// (becomes FlagByte, like Pithouse session 0x02).
        /// </summary>
        private void ProbeAndOpenSessions()
        {
            if (!_connection.IsConnected)
                return;

            byte mgmtPort = 0;
            byte telemetryPort = 0;
            int portsFound = 0;

            SimHub.Logging.Current.Info("[Moza] Probing for available SerialStream ports...");

            for (byte port = 1; port <= MaxProbePort && portsFound < 2; port++)
            {
                if (!_enabled || !_connection.IsConnected)
                    break;

                _ackReceived.Reset();
                _lastAckedSession = 0;

                // Send session open: session byte = port (they match for initial allocations)
                SendSessionOpen(port, port);

                // Wait for fc:00 ack
                if (_ackReceived.Wait(ProbeTimeoutMs))
                {
                    portsFound++;
                    if (portsFound == 1)
                    {
                        mgmtPort = port;
                        SimHub.Logging.Current.Info($"[Moza] Management session opened on port 0x{port:X2}");
                    }
                    else
                    {
                        telemetryPort = port;
                        SimHub.Logging.Current.Info($"[Moza] Telemetry session opened on port 0x{port:X2}");
                    }
                }
                else
                {
                    SimHub.Logging.Current.Debug($"[Moza] Port 0x{port:X2} not available (no ack)");
                }
            }

            if (telemetryPort != 0)
            {
                FlagByte = telemetryPort;
                SimHub.Logging.Current.Info($"[Moza] FlagByte set to 0x{FlagByte:X2} (telemetry port)");
            }
            else if (mgmtPort != 0)
            {
                // Only got one port — use it for telemetry
                FlagByte = mgmtPort;
                SimHub.Logging.Current.Warn($"[Moza] Only one port available (0x{mgmtPort:X2}), using for telemetry");
            }
            else
            {
                // No ports acked — fall back to 0x02 (post-power-cycle default)
                FlagByte = 0x02;
                SimHub.Logging.Current.Warn("[Moza] No ports responded to session open, falling back to 0x02");
            }
        }

        /// <summary>
        /// Send the tier definition message on the telemetry session.
        /// This is the critical config data that tells the wheel firmware how to
        /// decode each flag byte's bit-packed telemetry data: which channels are
        /// in each tier, their compression codes, and bit widths.
        ///
        /// Pithouse sends this as 7c:00 data chunks (type=0x01) on session 0x02
        /// during the first ~1s after session open. Without it, the wheel silently
        /// ignores all 7d:23 telemetry frames.
        /// </summary>
        private void SendTierDefinition()
        {
            var profile = _profile;
            if (profile == null || profile.Tiers.Count == 0)
                return;
            if (!_connection.IsConnected)
                return;

            byte[] message = TierDefinitionBuilder.BuildTierDefinitionMessage(profile, FlagByte);
            int seq = (int)FlagByte; // Start seq at FlagByte (matches Pithouse: session 0x02 starts seq at 3)
            // Actually Pithouse starts seq at 3 for sub-message 1 and 8 for sub-message 2.
            // We only send sub-message 2 (the full tier def). Pithouse's seq=8 corresponds
            // to the accumulated seq after sub-message 1 (7 chunks). We start our seq at
            // a value that won't conflict with the session open acks.
            seq = 3; // Match Pithouse's starting seq for config data

            var frames = TierDefinitionBuilder.ChunkMessage(message, FlagByte, ref seq);

            SimHub.Logging.Current.Info(
                $"[Moza] Sending tier definition: {message.Length} bytes in {frames.Count} chunks " +
                $"on session 0x{FlagByte:X2} ({profile.Tiers.Count} tiers)");

            foreach (var frame in frames)
                _connection.Send(frame);
        }

        /// <summary>
        /// Probe the Display sub-device inside the wheel.
        /// Pithouse sends the same identity commands used for the main wheel
        /// (0x09, 0x04, 0x06, 0x02, 0x05) but via group 0x43 to route them
        /// through the SerialStream to the Display sub-module.
        ///
        /// Responses arrive asynchronously via OnMessageDuringPreamble:
        /// - 0x87 data=01 "Display" → model name (confirms display present)
        /// - 0x89 data=00:01 → presence check (1 sub-device)
        /// - 0x82 data=02 → product type
        /// </summary>
        private void SendDisplayProbe()
        {
            if (!_connection.IsConnected) return;

            // Heartbeat/ping first
            _connection.Send(BuildDisplayFrame(0x00));

            // Identity probe: 0x09 → 0x04 → 0x06 → 0x02 → 0x05
            _connection.Send(BuildDisplayFrame(0x09));
            _connection.Send(BuildDisplayFrameWithData(0x04, new byte[] { 0x00, 0x00, 0x00, 0x00 }));
            _connection.Send(BuildDisplayFrame(0x06));
            _connection.Send(BuildDisplayFrameWithData(0x02, new byte[] { 0x00 }));
            _connection.Send(BuildDisplayFrameWithData(0x05, new byte[] { 0x00, 0x00, 0x00, 0x00 }));

            // Version queries: 0x07, 0x0F, 0x11, 0x08, 0x10 (sub-device 1)
            _connection.Send(BuildDisplayFrameWithData(0x07, new byte[] { 0x01 }));
            _connection.Send(BuildDisplayFrameWithData(0x0F, new byte[] { 0x01 }));
            _connection.Send(BuildDisplayFrameWithData(0x11, new byte[] { 0x04 }));
            _connection.Send(BuildDisplayFrameWithData(0x08, new byte[] { 0x01 }));
            _connection.Send(BuildDisplayFrameWithData(0x10, new byte[] { 0x00 }));
        }

        private byte[] BuildDisplayFrame(byte cmd)
        {
            var frame = new byte[] { MozaProtocol.MessageStart, 0x01,
                MozaProtocol.TelemetrySendGroup, MozaProtocol.DeviceWheel,
                cmd, 0x00 };
            frame[5] = MozaProtocol.CalculateChecksum(frame);
            return frame;
        }

        private byte[] BuildDisplayFrameWithData(byte cmd, byte[] data)
        {
            var frame = new byte[4 + 1 + data.Length + 1]; // start+N+grp+dev + cmd + data + checksum
            frame[0] = MozaProtocol.MessageStart;
            frame[1] = (byte)(1 + data.Length); // N = cmd + data
            frame[2] = MozaProtocol.TelemetrySendGroup;
            frame[3] = MozaProtocol.DeviceWheel;
            frame[4] = cmd;
            Array.Copy(data, 0, frame, 5, data.Length);
            frame[frame.Length - 1] = MozaProtocol.CalculateChecksum(frame);
            return frame;
        }

        // ── Preamble message handling ───────────────────────────────────────

        /// <summary>
        /// Handle incoming messages during port probing and the ~1s preamble phase.
        /// Detects fc:00 session acks (for port probing) and acks incoming 7c:00
        /// channel data on the telemetry session.
        /// </summary>
        private void OnMessageDuringPreamble(byte[] data)
        {
            if (!_enabled)
                return;

            // data layout from MozaSerialConnection: [group, device, cmdPayload...]
            if (data.Length < 4)
                return;

            // Only process 0xC3 (response to 0x43) from device 0x71 (nibble-swapped 0x17)
            if (data[0] != 0xC3 || data[1] != 0x71)
                return;

            byte cmd1 = data[2];
            byte cmd2 = data[3];

            // fc:00 ack — signals a session open was accepted
            if (cmd1 == 0xFC && cmd2 == 0x00 && data.Length >= 5)
            {
                _lastAckedSession = data[4];
                _ackReceived.Set();
                return;
            }

            // 7c:00 data chunks — ack channel registrations during preamble
            if (cmd1 == 0x7C && cmd2 == 0x00 && data.Length >= 8 && !_preambleComplete)
            {
                byte session = data[4];
                byte type = data[5];

                if (session == FlagByte && type == 0x01)
                {
                    int seq = data[6] | (data[7] << 8);
                    if (seq > _sessionAckSeq)
                        _sessionAckSeq = seq;
                    SendSessionAck(FlagByte, (ushort)_sessionAckSeq);
                }
                return;
            }

            // Display sub-device responses (identity probe answers)
            // cmd byte is data[2], which is the response group byte (request | 0x80)
            // 0x87 = model name response (to 0x07 query)
            if (data[2] == 0x87 && data.Length >= 5 && data[3] == 0x01)
            {
                // data[3] = sub-device index (0x01), data[4..] = null-terminated ASCII name
                int nameLen = 0;
                for (int k = 4; k < data.Length && data[k] != 0; k++)
                    nameLen++;
                if (nameLen > 0)
                {
                    _displayModelName = System.Text.Encoding.ASCII.GetString(data, 4, nameLen);
                    _displayDetected = true;
                    SimHub.Logging.Current.Info($"[Moza] Display sub-device detected: \"{_displayModelName}\"");
                }
            }
        }

        // ── Timer loop ──────────────────────────────────────────────────────

        private void OnTimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (!_enabled || !_connection.IsConnected)
                return;

            var tiers = _tiers;
            if (tiers == null || tiers.Length == 0)
                return;

            try
            {
                // Preamble phase: ~1 second of session ack processing + heartbeats.
                // No telemetry, enable, or channel config until preamble completes.
                if (!_preambleComplete)
                {
                    _tickCounter++;

                    int slowInterval = Math.Max(1, 1000 / _baseTickMs);
                    if (_tickCounter % slowInterval == 0)
                        SendHeartbeat();

                    if (_tickCounter >= _preambleTickTarget)
                    {
                        _preambleComplete = true;
                        _connection.MessageReceived -= OnMessageDuringPreamble;

                        // Channel config burst (matches Pithouse t=9.8-9.9)
                        SendChannelConfig();

                        _tickCounter = 0;
                        _modeCounter = 0;
                        _slowCounter = 0;
                    }
                    return;
                }

                // Active phase: telemetry + enable + periodic streams
                var snapshot = TestMode
                    ? Diagnostics.BuildTestPattern(_testFrameCounter++)
                    : GameDataSnapshot.FromStatusData(_latestGameData);

                for (int i = 0; i < tiers.Length; i++)
                {
                    var tier = tiers[i];
                    if (_tickCounter % tier.TickInterval != 0)
                        continue;

                    byte flagByte = (byte)(FlagByte + i);
                    byte[] frame = tier.Builder.BuildFrameFromSnapshot(snapshot, flagByte);
                    _connection.Send(frame);

                    if (i == 0)
                    {
                        LastFrameSent = frame;
                        _framesSent++;
                        Diagnostics.RecordFrame(frame);
                    }
                }

                _connection.Send(_cachedEnableFrame);

                if (SendSequenceCounter)
                    _connection.Send(BuildSequenceCounterFrame());

                _tickCounter++;

                if (SendTelemetryMode && (_modeCounter++ % 10 == 0))
                    _connection.Send(_cachedModeFrame);

                int slow = Math.Max(1, 1000 / _baseTickMs);
                if (_slowCounter++ % slow == 0)
                {
                    SendHeartbeat();
                    SendDashKeepalive();
                    SendDisplayConfig();
                    SendStatusPush();
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Moza] Telemetry send error: {ex.Message}");
            }
        }

        // ── Session management ──────────────────────────────────────────────

        private void SendSessionOpen(byte session, byte port)
        {
            var frame = new byte[]
            {
                MozaProtocol.MessageStart, 0x0A,
                MozaProtocol.TelemetrySendGroup, MozaProtocol.DeviceWheel,
                0x7C, 0x00,
                session, 0x81,          // session byte + type (channel open)
                port, 0x00,             // seq = port (LE)
                port, 0x00,             // session_id = port (LE)
                0xFD, 0x02,             // receive_window = 765 (LE)
                0x00                    // checksum placeholder
            };
            frame[14] = MozaProtocol.CalculateChecksum(frame);
            _connection.Send(frame);
        }

        private void SendSessionAck(byte session, ushort ackSeq)
        {
            var frame = new byte[]
            {
                MozaProtocol.MessageStart, 0x05,
                MozaProtocol.TelemetrySendGroup, MozaProtocol.DeviceWheel,
                0xFC, 0x00,
                session,
                (byte)(ackSeq & 0xFF),
                (byte)(ackSeq >> 8),
                0x00
            };
            frame[9] = MozaProtocol.CalculateChecksum(frame);
            _connection.Send(frame);
        }

        // ── Channel configuration ───────────────────────────────────────────

        private void SendChannelConfig()
        {
            if (!_connection.IsConnected)
                return;

            var profile = _profile;
            if (profile == null || profile.Tiers.Count == 0)
                return;

            for (int page = 0; page <= 1; page++)
            {
                for (byte cc = 2; cc <= 5; cc++)
                    _connection.Send(BuildChannelEnableFrame((byte)page, cc));
            }

            _connection.Send(BuildGroup40Frame(0x09, 0x00));
            _connection.Send(_cachedModeFrame);
        }

        private byte[] BuildChannelEnableFrame(byte page, byte channelIndex)
        {
            var frame = new System.Collections.Generic.List<byte>
            {
                MozaProtocol.MessageStart, 5,
                MozaProtocol.TelemetryModeGroup, MozaProtocol.DeviceWheel,
                0x1E, page,
                channelIndex, 0x00, 0x00,
            };
            frame.Add(MozaProtocol.CalculateChecksum(frame.ToArray()));
            return frame.ToArray();
        }

        private byte[] BuildGroup40Frame(byte cmd1, byte cmd2)
        {
            var frame = new System.Collections.Generic.List<byte>
            {
                MozaProtocol.MessageStart, 2,
                MozaProtocol.TelemetryModeGroup, MozaProtocol.DeviceWheel,
                cmd1, cmd2,
            };
            frame.Add(MozaProtocol.CalculateChecksum(frame.ToArray()));
            return frame.ToArray();
        }

        // ── Cached frame construction ───────────────────────────────────────

        private void BuildCachedFrames()
        {
            _cachedModeFrame = BuildStaticFrame(new byte[] {
                MozaProtocol.MessageStart, 4,
                MozaProtocol.TelemetryModeGroup, MozaProtocol.DeviceWheel,
                0x28, 0x02, 0x01, 0x00 });

            _cachedEnableFrame = BuildStaticFrame(new byte[] {
                MozaProtocol.MessageStart, 6,
                MozaProtocol.BaseSendTelemetry, MozaProtocol.DeviceWheel,
                0xFD, 0xDE, 0x00, 0x00, 0x00, 0x00 });

            _cachedSequenceFrame = BuildStaticFrame(new byte[] {
                MozaProtocol.MessageStart, 6,
                0x2D, MozaProtocol.DeviceBase,
                0xF5, 0x31, 0x00, 0x00, 0x00, 0x00 });

            _cachedHeartbeatFrames = new byte[13][];
            for (int i = 0; i < 13; i++)
            {
                byte dev = (byte)(18 + i);
                var frame = new byte[] { MozaProtocol.MessageStart, 0x00, 0x00, dev, 0x00 };
                frame[4] = MozaProtocol.CalculateChecksum(frame);
                _cachedHeartbeatFrames[i] = frame;
            }
        }

        private static byte[] BuildStaticFrame(byte[] body)
        {
            var frame = new byte[body.Length + 1];
            Array.Copy(body, 0, frame, 0, body.Length);
            frame[frame.Length - 1] = MozaProtocol.CalculateChecksum(body);
            return frame;
        }

        private byte[] BuildSequenceCounterFrame()
        {
            byte seq = _sequenceCounter++;
            _cachedSequenceFrame[9] = seq;
            _cachedSequenceFrame[10] = MozaProtocol.CalculateChecksum(
                _cachedSequenceFrame, _cachedSequenceFrame.Length - 1);
            // Return a copy: the write queue holds a reference until the write thread
            // drains it, and we mutate _cachedSequenceFrame on the next tick.
            var copy = new byte[_cachedSequenceFrame.Length];
            Array.Copy(_cachedSequenceFrame, copy, copy.Length);
            return copy;
        }

        // ── Periodic streams ────────────────────────────────────────────────

        public volatile int DetectedDeviceMask;

        private void SendHeartbeat()
        {
            int mask = DetectedDeviceMask;
            for (int i = 0; i < _cachedHeartbeatFrames.Length; i++)
            {
                if (mask == 0 || (mask & (1 << i)) != 0)
                    _connection.Send(_cachedHeartbeatFrames[i]);
            }
        }

        private void SendDashKeepalive()
        {
            foreach (byte dev in new byte[] { MozaProtocol.DeviceDash, 0x15 })
            {
                var frame = new byte[] { MozaProtocol.MessageStart, 0x01, MozaProtocol.TelemetrySendGroup, dev, 0x00, 0x00 };
                frame[5] = MozaProtocol.CalculateChecksum(frame);
                _connection.Send(frame);
            }
        }

        private void SendDisplayConfig()
        {
            int pageCount = _profile?.PageCount ?? 1;
            int page = _displayConfigPage % pageCount;
            _displayConfigPage++;

            byte b2 = (byte)(0x05 + 2 * page);
            byte b4 = (byte)(0x03 + 2 * page);
            byte z  = (byte)(0x06 + 2 * page);

            var frame1 = new byte[] { MozaProtocol.MessageStart, 0x0A, MozaProtocol.TelemetrySendGroup,
                MozaProtocol.DeviceWheel, 0x7C, 0x27, 0x0F, 0x80, b2, 0x00, b4, 0x00, 0xFE, 0x01, 0x00 };
            frame1[14] = MozaProtocol.CalculateChecksum(frame1);
            _connection.Send(frame1);

            var frame2 = new byte[] { MozaProtocol.MessageStart, 0x06, MozaProtocol.TelemetrySendGroup,
                MozaProtocol.DeviceWheel, 0x7C, 0x27, 0x0F, 0x00, z, 0x00, 0x00 };
            frame2[10] = MozaProtocol.CalculateChecksum(frame2);
            _connection.Send(frame2);
        }

        private void SendStatusPush()
        {
            var frame = new byte[] { MozaProtocol.MessageStart, 0x05, MozaProtocol.TelemetrySendGroup,
                MozaProtocol.DeviceWheel, 0xFC, 0x00, 0x00, 0x00, 0x00, 0x00 };
            frame[9] = MozaProtocol.CalculateChecksum(frame);
            _connection.Send(frame);
        }

        public void Dispose()
        {
            Stop();
            _ackReceived.Dispose();
        }

        private class TierState
        {
            public TelemetryFrameBuilder Builder = null!;
            public int TickInterval;
        }
    }
}
