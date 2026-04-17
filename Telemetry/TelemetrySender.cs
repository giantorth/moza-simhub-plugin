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
    ///   3. Send session preamble (sub-message 1) then tier definition on telemetry session
    ///   4. Ack incoming channel data on telemetry session with fc:00 (~1 second)
    ///   5. Send 0x40 channel config burst (1E enables, 28:00, 28:01, 09:00, 28:02)
    ///   6. Begin 0x41 enable signal + 7d:23 telemetry with flag=0x00+tier
    ///
    /// Port allocation: the wheel uses a global monotonic counter shared between
    /// host and device. We probe from port 1 upward, sending a type=0x81 session
    /// open and waiting ~50ms for an fc:00 ack. The first port that gets acked
    /// becomes the management session; the next acked port becomes the telemetry
    /// session (FlagByte). The session port identifies which 7c:00 stream carries
    /// config data; the flag bytes inside tier definitions and telemetry frames
    /// are always 0-based (0x00, 0x01, 0x02), independent of the session port.
    ///
    /// Each tier in the MultiStreamProfile runs at its own rate derived from package_level.
    /// Flag bytes are TierFlagBase + tier index (sorted by package_level ascending).
    /// TierFlagBase depends on FlagByteMode setting — see § TelemetryFlagByteMode.
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

        // Upload handshake state
        private int _mgmtAckSeq;
        private readonly ManualResetEventSlim _mgmtResponseEvent = new ManualResetEventSlim(false);

        // Display sub-device detection
        private volatile bool _displayDetected;
        private string _displayModelName = "";

        // Wheel channel catalog (parsed from incoming 7c:00 session data during preamble)
        private System.Collections.Generic.List<byte> _incomingSessionBuffer = new();
        private volatile System.Collections.Generic.List<string>? _wheelChannelCatalog;

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

        // Session ports determined during port probing.
        // MgmtPort = first acked port (session 0x01, used for dashboard upload).
        // FlagByte = second acked port (session 0x02, used for tier definitions and fc:00 acks).
        private byte _mgmtPort;
        public byte FlagByte { get; set; } = 0x02;
        public bool SendTelemetryMode { get; set; } = true;
        public bool SendSequenceCounter { get; set; } = true;
        public bool TestMode { get; set; } = false;

        /// <summary>
        /// How flag bytes are assigned in tier definitions and telemetry frames.
        /// 0 = Zero-based (0x00+). 1 = Session-port-based (FlagByte+). 2 = Two-batch (Pithouse-style).
        /// Only applies to protocol version 2. Version 0 always uses zero-based flags.
        /// </summary>
        public int FlagByteMode { get; set; } = 0;

        /// <summary>
        /// Tier definition protocol version.
        /// 0 = URL-based subscription (send channel URLs, wheel resolves compression).
        /// 2 = Compact numeric (send flag bytes, channel indices, compression codes, bit widths).
        /// </summary>
        public int ProtocolVersion { get; set; } = 0;

        /// <summary>Channel URLs reported by the wheel during session startup. Null until parsed.</summary>
        public System.Collections.Generic.IReadOnlyList<string>? WheelChannelCatalog => _wheelChannelCatalog;

        /// <summary>Raw .mzdash file content for upload to the wheel. Set by ApplyTelemetrySettings.</summary>
        public byte[]? MzdashContent { get; set; }

        /// <summary>Dashboard name (used for logging). Set by ApplyTelemetrySettings.</summary>
        public string MzdashName { get; set; } = "";

        /// <summary>Whether to upload the dashboard to the wheel on startup.</summary>
        public bool UploadDashboard { get; set; } = true;

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
            lock (_incomingSessionBuffer) { _incomingSessionBuffer.Clear(); }
            _wheelChannelCatalog = null;
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

            // Upload the dashboard file to the wheel on session 0x01 (mgmt port).
            // PitHouse does this on every connection — the wheel may require a fresh
            // upload before accepting tier definitions or telemetry frames.
            if (UploadDashboard && MzdashContent != null && _mgmtPort != 0)
                SendDashboardUpload();

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
            // Reset so StartTelemetryIfReady() won't skip us on re-enable
            _framesSent = 0;
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

                // Wait for an fc:00 ack whose session byte matches THIS port. The wheel
                // can emit stray acks (late response to a prior probe, or an ack for a
                // previously-opened session); attributing one of those to the current
                // port would lock the plugin onto a session the wheel never agreed to
                // and silently break all downstream tier-def / telemetry.
                var deadline = DateTime.UtcNow.AddMilliseconds(ProbeTimeoutMs);
                bool acked = false;
                while (true)
                {
                    int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (remaining <= 0 || !_ackReceived.Wait(remaining))
                        break;

                    if (_lastAckedSession == port)
                    {
                        acked = true;
                        break;
                    }

                    // Stale ack (different session) — discard and keep waiting within the
                    // remaining timeout in case the ack for THIS port is still in flight.
                    SimHub.Logging.Current.Debug(
                        $"[Moza] Probing port 0x{port:X2}: ignoring stale ack for session 0x{_lastAckedSession:X2}");
                    _ackReceived.Reset();
                    _lastAckedSession = 0;
                }

                if (acked)
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

            _mgmtPort = mgmtPort;

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
        /// <summary>
        /// Compute the flag byte base for tier definitions and telemetry frames.
        /// Returns the value to pass to BuildTierDefinitionMessage and to add to
        /// the tier index in the telemetry send loop.
        /// </summary>
        private byte TierFlagBase =>
            FlagByteMode == 1 ? FlagByte : (byte)0x00;

        private void SendTierDefinition()
        {
            var profile = _profile;
            if (profile == null || profile.Tiers.Count == 0)
                return;
            if (!_connection.IsConnected)
                return;

            int seq = 3; // Match Pithouse's starting seq for config data

            if (ProtocolVersion == 0)
            {
                // Version 0: URL-based subscription.
                // The sentinel (0xFF) and tag 0x03 (value=1) are inline in the message.
                // No separate tag 0x07/0x03 preamble needed.
                byte[] message = TierDefinitionBuilder.BuildV0UrlSubscription(profile);
                var frames = TierDefinitionBuilder.ChunkMessage(message, FlagByte, ref seq);

                int channelCount = 0;
                foreach (var t in profile.Tiers) channelCount += t.Channels.Count;
                SimHub.Logging.Current.Info(
                    $"[Moza] Sending v0 URL subscription: " +
                    $"{message.Length} bytes in {frames.Count} chunks " +
                    $"on session 0x{FlagByte:X2} ({channelCount} channels)");

                foreach (var frame in frames)
                    _connection.Send(frame);
            }
            else
            {
                // Version 2: compact numeric tier definitions.
                // Sub-message 1 preamble: tag 0x07 (version=2), tag 0x03 (value=0).
                byte[] preambleMsg = new byte[]
                {
                    0x07, 0x04, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00,
                    0x03, 0x00, 0x00, 0x00, 0x00
                };
                var preambleFrames = TierDefinitionBuilder.ChunkMessage(preambleMsg, FlagByte, ref seq);
                foreach (var frame in preambleFrames)
                    _connection.Send(frame);

                byte flagBase = TierFlagBase;

                if (FlagByteMode == 2)
                {
                    byte[] probeMsg = TierDefinitionBuilder.BuildProbeBatch(profile, 0x00);
                    var probeFrames = TierDefinitionBuilder.ChunkMessage(probeMsg, FlagByte, ref seq);
                    foreach (var frame in probeFrames)
                        _connection.Send(frame);
                    flagBase = FlagByte;
                }

                byte[] message = TierDefinitionBuilder.BuildTierDefinitionMessage(profile, flagBase);
                var frames = TierDefinitionBuilder.ChunkMessage(message, FlagByte, ref seq);

                SimHub.Logging.Current.Info(
                    $"[Moza] Sending v2 tier definition: mode={FlagByteMode} flagBase=0x{flagBase:X2}, " +
                    $"preamble ({preambleFrames.Count} chunks) + " +
                    $"{message.Length} bytes in {frames.Count} chunks " +
                    $"on session 0x{FlagByte:X2} ({profile.Tiers.Count} tiers)");

                foreach (var frame in frames)
                    _connection.Send(frame);
            }
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
        /// <summary>
        /// Upload the .mzdash dashboard file to the wheel on session 0x01.
        /// PitHouse does this on every connection. The upload uses FF-prefixed
        /// sub-message framing with CRC-32 verification.
        /// </summary>
        private void SendDashboardUpload()
        {
            var content = MzdashContent;
            if (content == null || content.Length == 0)
                return;
            if (!_connection.IsConnected || _mgmtPort == 0)
                return;

            // Field 0 tokens: PitHouse sends [random_u32 | 0x00000002] [unix_timestamp | 0x00000000].
            // Token 1: random nonce in low 32 bits, constant 0x02 in high 32 bits.
            // Token 2: Unix timestamp in low 32 bits, zero in high 32 bits.
            // These are correlation IDs — the wheel doesn't validate them.
            uint nonce = (uint)Environment.TickCount ^ (uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            ulong token1 = ((ulong)0x00000002 << 32) | nonce;
            ulong token2 = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            byte[] message = DashboardUploader.BuildUploadMessage(content, token1, token2);

            int seq = 2; // Session 0x01 starts at seq 2 (seq 0-1 used by session open)
            var frames = TierDefinitionBuilder.ChunkMessage(message, _mgmtPort, ref seq);

            SimHub.Logging.Current.Info(
                $"[Moza] Uploading dashboard \"{MzdashName}\": " +
                $"{content.Length} bytes raw, {message.Length} bytes wire, " +
                $"{frames.Count} chunks on session 0x{_mgmtPort:X2}");

            // Reset handshake state
            _mgmtAckSeq = 0;
            _mgmtResponseEvent.Reset();

            // Send all upload chunks
            foreach (var frame in frames)
            {
                if (!_enabled || !_connection.IsConnected) return;
                _connection.Send(frame);
            }

            // Wait for the wheel to respond (ack or echo the upload)
            // PitHouse waits for device response between sub-messages.
            // We send everything and then wait for any response within 2 seconds.
            if (_enabled)
            {
                if (_mgmtResponseEvent.Wait(2000))
                    SimHub.Logging.Current.Info($"[Moza] Dashboard upload acknowledged (ack seq={_mgmtAckSeq})");
                else
                    SimHub.Logging.Current.Warn("[Moza] Dashboard upload: no response from wheel within 2s");
            }

            // Send type=0x00 end marker on the mgmt session
            var endMarker = new byte[]
            {
                MozaProtocol.MessageStart, 0x06,
                MozaProtocol.TelemetrySendGroup, MozaProtocol.DeviceWheel,
                0x7C, 0x00,
                _mgmtPort, 0x00, // type=0x00 (end marker)
                0x00
            };
            endMarker[endMarker.Length - 1] = MozaProtocol.CalculateChecksum(endMarker);
            _connection.Send(endMarker);
        }

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

            // 7c:00 data chunks — ack and buffer during preamble/upload
            if (cmd1 == 0x7C && cmd2 == 0x00 && data.Length >= 8)
            {
                byte session = data[4];
                byte type = data[5];

                if (type == 0x01)
                {
                    int seq = data[6] | (data[7] << 8);

                    // Ack on the telemetry session
                    if (session == FlagByte)
                    {
                        if (seq > _sessionAckSeq)
                            _sessionAckSeq = seq;
                        SendSessionAck(FlagByte, (ushort)_sessionAckSeq);
                    }

                    // Ack on the management session (upload handshake)
                    if (session == _mgmtPort && _mgmtPort != 0)
                    {
                        if (seq > _mgmtAckSeq)
                            _mgmtAckSeq = seq;
                        SendSessionAck(_mgmtPort, (ushort)_mgmtAckSeq);
                        _mgmtResponseEvent.Set();
                    }

                    // Buffer the chunk payload (strip CRC) for channel catalog parsing.
                    if (session == FlagByte && data.Length > 12 && !_preambleComplete)
                    {
                        byte[] raw = new byte[data.Length - 8];
                        Array.Copy(data, 8, raw, 0, raw.Length);
                        if (raw.Length >= 5)
                        {
                            int netLen = raw.Length - 4;
                            lock (_incomingSessionBuffer)
                            {
                                for (int k = 0; k < netLen; k++)
                                    _incomingSessionBuffer.Add(raw[k]);
                            }
                        }
                    }
                }

                // Type 0x00 = end marker (upload complete signal from wheel)
                if (type == 0x00 && session == _mgmtPort)
                {
                    _mgmtResponseEvent.Set();
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

        /// <summary>
        /// Parse the wheel's channel catalog from the buffered incoming 7c:00 session data.
        /// The wheel sends tag 0x04 entries with channel URLs during the preamble.
        /// </summary>
        private void ParseWheelChannelCatalog()
        {
            byte[] buffer;
            lock (_incomingSessionBuffer)
            {
                if (_incomingSessionBuffer.Count == 0) return;
                buffer = _incomingSessionBuffer.ToArray();
            }

            var channels = new System.Collections.Generic.List<string>();
            int i = 0;
            while (i < buffer.Length)
            {
                byte tag = buffer[i];
                if (tag == 0xFF)
                {
                    i++;
                    continue;
                }
                if (i + 5 > buffer.Length) break;
                uint param = (uint)(buffer[i + 1] | (buffer[i + 2] << 8) |
                             (buffer[i + 3] << 16) | (buffer[i + 4] << 24));

                if (tag == 0x04 && i + 5 + (int)param <= buffer.Length && param > 1 && param < 200)
                {
                    // Channel URL: [ch_index:u8] [url:ASCII]
                    int urlLen = (int)param - 1;
                    string url = System.Text.Encoding.ASCII.GetString(buffer, i + 6, urlLen);
                    // Extract just the suffix after the last /
                    int slash = url.LastIndexOf('/');
                    string name = slash >= 0 ? url.Substring(slash + 1) : url;
                    channels.Add(name);
                    i += 5 + (int)param;
                }
                else if (tag == 0x06)
                {
                    break; // end marker
                }
                else if (tag == 0x03 && param <= 8)
                {
                    i += 5 + (int)param;
                }
                else
                {
                    // Unknown tag — skip by trying param as size
                    if (param < 200)
                        i += 5 + (int)param;
                    else
                        break;
                }
            }

            if (channels.Count > 0)
            {
                _wheelChannelCatalog = channels;
                SimHub.Logging.Current.Info(
                    $"[Moza] Wheel channel catalog ({channels.Count}): {string.Join(", ", channels)}");
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

                        // Parse the wheel's channel catalog from buffered session data
                        ParseWheelChannelCatalog();

                        // Version 0: re-send URL subscription (Pithouse double-sends)
                        if (ProtocolVersion == 0)
                            SendTierDefinition();

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

                    byte flagByte = (byte)(TierFlagBase + i);
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

            // 28:00 = WheelGetCfg_GetMultiFunctionSwitch — query active dashboard mode
            // 28:01 = WheelGetCfg_GetMultiFunctionNum — query active page number
            // (rs21_parameter.db [64,40,0/1]). The wheel retains the last loaded
            // dashboard across disconnections; Pithouse reads the current state before
            // setting 28:02 (telemetry channel mode: 01=multi-channel, 00=RPM only).
            _connection.Send(BuildGroup40Frame3(0x28, 0x00, 0x00));
            _connection.Send(BuildGroup40Frame3(0x28, 0x01, 0x00));
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

        private byte[] BuildGroup40Frame3(byte cmd1, byte cmd2, byte cmd3)
        {
            var frame = new System.Collections.Generic.List<byte>
            {
                MozaProtocol.MessageStart, 3,
                MozaProtocol.TelemetryModeGroup, MozaProtocol.DeviceWheel,
                cmd1, cmd2, cmd3,
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
            // TelemetryServer periodic connection ping (group 0x43, N=1, data=0x00).
            // Pithouse sends to 0x14 (dash), 0x15, and 0x17 (wheel) every ~1.1s.
            // Distinct from group 0x00 heartbeats and SerialStream fc:00 acks.
            // Unclear whether the wheel requires this for telemetry to flow, but
            // Pithouse sends it consistently (~15× per session).
            foreach (byte dev in new byte[] { MozaProtocol.DeviceDash, 0x15, MozaProtocol.DeviceWheel })
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
            // Pithouse's fc:00 frames are purely reactive session acks — there is no
            // separate "active-phase status sender." The ack_seq tracks the highest
            // sequence received on this session. Sending periodically with the current
            // ack_seq is harmless (just re-acks the same point if no new data arrived).
            SendSessionAck(FlagByte, (ushort)_sessionAckSeq);
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
