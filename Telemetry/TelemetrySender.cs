using System;
using System.Timers;
using GameReaderCommon;
using MozaPlugin.Protocol;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Periodically encodes game data and sends telemetry frames to the wheel.
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

        // Settings
        public byte FlagByte { get; set; } = 0x01;
        public bool SendTelemetryMode { get; set; } = true;
        public bool SendSequenceCounter { get; set; } = true;
        public bool TestMode { get; set; } = false;

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

                // Timer ticks at the fastest tier's rate (lowest package_level)
                _baseTickMs = value.Tiers[0].PackageLevel;

                _tiers = new TierState[value.Tiers.Count];
                for (int i = 0; i < value.Tiers.Count; i++)
                {
                    var tier = value.Tiers[i];
                    // How many base ticks between sends for this tier
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

        /// <summary>Incremented each time the fastest-tier frame is sent, for UI display.</summary>
        private volatile int _framesSent;
        public int FramesSent => _framesSent;

        /// <summary>The last fastest-tier frame bytes sent, for diagnostic display.</summary>
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

            SendChannelConfig();

            double intervalMs = _baseTickMs;
            _sendTimer = new Timer(intervalMs) { AutoReset = true };
            _sendTimer.Elapsed += OnTimerElapsed;
            _sendTimer.Start();
        }

        public void Stop()
        {
            _enabled = false;
            if (_sendTimer != null)
            {
                _sendTimer.Stop();
                _sendTimer.Elapsed -= OnTimerElapsed;
                _sendTimer.Dispose();
                _sendTimer = null;
            }
        }

        /// <summary>Called from plugin DataUpdate — stores the latest game data.</summary>
        public void UpdateGameData(StatusDataBase? data)
        {
            _latestGameData = data;
        }

        private void OnTimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (!_enabled || !_connection.IsConnected)
                return;

            var tiers = _tiers;
            if (tiers == null || tiers.Length == 0)
                return;

            try
            {
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

                    // Track the fastest tier (index 0) for diagnostics
                    if (i == 0)
                    {
                        LastFrameSent = frame;
                        _framesSent++;
                        Diagnostics.RecordFrame(frame);
                    }
                }

                // Telemetry enable signal — Pithouse sends this at ~48 Hz the entire session.
                // Without it the wheel may ignore 0x43/7D:23 data frames.
                _connection.Send(BuildTelemetryEnableFrame());

                // Sequence counter to the base unit (~30 Hz, matching our tick rate)
                if (SendSequenceCounter)
                    _connection.Send(BuildSequenceCounterFrame());

                _tickCounter++;

                // Periodically send telemetry mode frame to keep wheel in multi-channel mode
                if (SendTelemetryMode && (_modeCounter++ % 10 == 0))
                    _connection.Send(BuildTelemetryModeFrame());

                // ~1 Hz slow streams: heartbeat, keepalive, display config, status push
                int slowInterval = Math.Max(1, 1000 / _baseTickMs);
                if (_slowCounter++ % slowInterval == 0)
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

        /// <summary>
        /// Send the channel configuration burst observed in Pit House captures.
        /// Declares which channel groups are active on each page, then sets
        /// multi-channel telemetry mode. Pit House sends this on every wheel
        /// connection before telemetry frames flow.
        ///
        /// From captures: indices 2-5 are sent on pages 0 and 1 with data CC 00 00.
        /// The wheel responds with stored interval values (500, 1000, 3000) confirming
        /// it knows those channels from its stored dashboard config.
        /// </summary>
        private void SendChannelConfig()
        {
            if (!_connection.IsConnected)
                return;

            var profile = _profile;
            if (profile == null || profile.Tiers.Count == 0)
                return;

            // 1e:page data=CC0000 — enable stream group CC on each page.
            // All captures show fixed values 2-5 regardless of dashboard or wheel type.
            // The m Formula 1 dashboard has 2 tiers (9ch + 6ch) but CC=2,3,4,5 are
            // always sent — these appear to be fixed stream slot identifiers.
            for (int page = 0; page <= 1; page++)
            {
                for (byte cc = 2; cc <= 5; cc++)
                    _connection.Send(BuildChannelEnableFrame((byte)page, cc));
            }

            // 09:00 — config mode
            _connection.Send(BuildGroup40Frame(0x09, 0x00));

            // 28:02 01 00 — set multi-channel telemetry mode
            _connection.Send(BuildTelemetryModeFrame());
        }

        private byte[] BuildChannelEnableFrame(byte page, byte channelIndex)
        {
            // 7E 05 40 17 1E [page] [channel] 00 00 [checksum]
            var frame = new System.Collections.Generic.List<byte>
            {
                MozaProtocol.MessageStart,       // 7E
                5,                               // N = cmd(2) + data(3)
                MozaProtocol.TelemetryModeGroup, // 40
                MozaProtocol.DeviceWheel,        // 17
                0x1E, page,                      // cmd: channel enable on page
                channelIndex, 0x00, 0x00,        // data: channel index + zeros
            };
            frame.Add(MozaProtocol.CalculateChecksum(frame.ToArray()));
            return frame.ToArray();
        }

        private byte[] BuildGroup40Frame(byte cmd1, byte cmd2)
        {
            var frame = new System.Collections.Generic.List<byte>
            {
                MozaProtocol.MessageStart,       // 7E
                2,                               // N = cmd only
                MozaProtocol.TelemetryModeGroup, // 40
                MozaProtocol.DeviceWheel,        // 17
                cmd1, cmd2,
            };
            frame.Add(MozaProtocol.CalculateChecksum(frame.ToArray()));
            return frame.ToArray();
        }

        private byte[] BuildTelemetryModeFrame()
        {
            // 7E 04 40 17 28 02 01 00 [checksum]
            var frame = new System.Collections.Generic.List<byte>
            {
                MozaProtocol.MessageStart,       // 7E
                4,                               // N
                MozaProtocol.TelemetryModeGroup, // 40
                MozaProtocol.DeviceWheel,        // 17
                0x28, 0x02,                      // cmd: set telemetry mode
                0x01, 0x00,                      // data: multi-channel mode
            };
            frame.Add(MozaProtocol.CalculateChecksum(frame.ToArray()));
            return frame.ToArray();
        }

        private byte[] BuildTelemetryEnableFrame()
        {
            // 7E 06 41 17 FD DE 00 00 00 00 [checksum]
            // Pithouse sends this at ~48 Hz the entire telemetry session.
            var frame = new System.Collections.Generic.List<byte>
            {
                MozaProtocol.MessageStart,        // 7E
                6,                                // N = cmd(2) + data(4)
                MozaProtocol.BaseSendTelemetry,   // 41
                MozaProtocol.DeviceWheel,         // 17
                0xFD, 0xDE,                       // cmd: dash telemetry enable
                0x00, 0x00, 0x00, 0x00,           // data: always zero
            };
            frame.Add(MozaProtocol.CalculateChecksum(frame.ToArray()));
            return frame.ToArray();
        }

        private byte[] BuildSequenceCounterFrame()
        {
            // 7E 06 2D 13 F5 31 00 00 00 XX [checksum]
            // Pithouse sends this at ~47 Hz; XX increments each send.
            byte seq = _sequenceCounter++;
            var frame = new System.Collections.Generic.List<byte>
            {
                MozaProtocol.MessageStart,  // 7E
                6,                          // N = cmd(2) + data(4)
                0x2D,                       // group: sequence counter
                MozaProtocol.DeviceBase,    // 13
                0xF5, 0x31,                 // cmd ID
                0x00, 0x00, 0x00, seq,      // data: counter in last byte
            };
            frame.Add(MozaProtocol.CalculateChecksum(frame.ToArray()));
            return frame.ToArray();
        }

        /// <summary>
        /// Send heartbeat (group 0x00, n=0) to all device IDs 18–30.
        /// Pithouse does this ~once per second as a keep-alive/presence check.
        /// </summary>
        private void SendHeartbeat()
        {
            for (byte dev = 18; dev <= 30; dev++)
            {
                var frame = new byte[] { MozaProtocol.MessageStart, 0x00, 0x00, dev, 0x00 };
                frame[4] = MozaProtocol.CalculateChecksum(frame);
                _connection.Send(frame);
            }
        }

        /// <summary>
        /// Send keepalive (group 0x43, n=1, payload 0x00) to dash (0x14) and dev21 (0x15).
        /// Pithouse sends these ~1.5/s during active telemetry.
        /// </summary>
        private void SendDashKeepalive()
        {
            foreach (byte dev in new byte[] { MozaProtocol.DeviceDash, 0x15 })
            {
                var frame = new byte[] { MozaProtocol.MessageStart, 0x01, MozaProtocol.TelemetrySendGroup, dev, 0x00, 0x00 };
                frame[5] = MozaProtocol.CalculateChecksum(frame);
                _connection.Send(frame);
            }
        }

        /// <summary>
        /// Send display config push (group 0x43, cmd 7C:27) to the wheel.
        /// Pithouse sends two payloads per page, cycling through pages ~1/s.
        /// Byte values are page-derived: byte2 = 5 + 2*page, byte4 = 3 + 2*page,
        /// companion byte2 = 6 + 2*page. Confirmed across 1-page and 3-page dashboards.
        /// </summary>
        private void SendDisplayConfig()
        {
            int pageCount = _profile?.PageCount ?? 1;
            int page = _displayConfigPage % pageCount;
            _displayConfigPage++;

            byte b2 = (byte)(0x05 + 2 * page);
            byte b4 = (byte)(0x03 + 2 * page);
            byte z  = (byte)(0x06 + 2 * page);

            // 7E 0A 43 17 7C 27 0F 80 [b2] 00 [b4] 00 FE 01 [checksum]
            var frame1 = new byte[] { MozaProtocol.MessageStart, 0x0A, MozaProtocol.TelemetrySendGroup,
                MozaProtocol.DeviceWheel, 0x7C, 0x27, 0x0F, 0x80, b2, 0x00, b4, 0x00, 0xFE, 0x01, 0x00 };
            frame1[14] = MozaProtocol.CalculateChecksum(frame1);
            _connection.Send(frame1);

            // 7E 06 43 17 7C 27 0F 00 [z] 00 [checksum]
            var frame2 = new byte[] { MozaProtocol.MessageStart, 0x06, MozaProtocol.TelemetrySendGroup,
                MozaProtocol.DeviceWheel, 0x7C, 0x27, 0x0F, 0x00, z, 0x00, 0x00 };
            frame2[10] = MozaProtocol.CalculateChecksum(frame2);
            _connection.Send(frame2);
        }

        /// <summary>
        /// Send status push (group 0x43, cmd FC:00) to the wheel.
        /// Pithouse sends this ~1/s. Data is session(1) + ack_seq(2 LE);
        /// we send zeros since we haven't done a file transfer session.
        /// </summary>
        private void SendStatusPush()
        {
            // 7E 05 43 17 FC 00 00 00 00 [checksum]
            var frame = new byte[] { MozaProtocol.MessageStart, 0x05, MozaProtocol.TelemetrySendGroup,
                MozaProtocol.DeviceWheel, 0xFC, 0x00, 0x00, 0x00, 0x00, 0x00 };
            frame[9] = MozaProtocol.CalculateChecksum(frame);
            _connection.Send(frame);
        }

        public void Dispose()
        {
            Stop();
        }

        private class TierState
        {
            public TelemetryFrameBuilder Builder = null!;
            public int TickInterval;
        }
    }
}
