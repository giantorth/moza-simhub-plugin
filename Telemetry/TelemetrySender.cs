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
        private int _baseTickMs;  // Timer period derived from fastest tier's package_level

        // Settings
        public byte FlagByte { get; set; } = 0x01;
        public bool SendTelemetryMode { get; set; } = true;
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

                _tickCounter++;

                // Periodically send telemetry mode frame to keep wheel in multi-channel mode
                if (SendTelemetryMode && (_modeCounter++ % 10 == 0))
                    _connection.Send(BuildTelemetryModeFrame());
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
