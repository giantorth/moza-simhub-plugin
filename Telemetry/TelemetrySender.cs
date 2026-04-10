using System;
using System.Collections.Generic;
using System.Timers;
using GameReaderCommon;
using MozaPlugin.Protocol;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Periodically encodes game data and sends telemetry frames to the wheel.
    /// </summary>
    public class TelemetrySender : IDisposable
    {
        private readonly MozaSerialConnection _connection;
        private Timer? _sendTimer;
        private TelemetryFrameBuilder? _frameBuilder;
        private volatile StatusDataBase? _latestGameData;
        private volatile bool _enabled;
        private int _modeCounter;
        private int _testFrameCounter;

        // Settings
        public byte FlagByte { get; set; } = 0x01;
        public int SendRateHz { get; set; } = 20;
        public bool SendTelemetryMode { get; set; } = true;
        public bool TestMode { get; set; } = false;

        public DashboardProfile? Profile
        {
            get => _frameBuilder?.Profile;
            set => _frameBuilder = value != null ? new TelemetryFrameBuilder(value) : null;
        }

        /// <summary>Incremented each time a frame is sent, for UI display.</summary>
        private volatile int _framesSent;
        public int FramesSent => _framesSent;

        /// <summary>The last frame bytes sent, for diagnostic display.</summary>
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
            _modeCounter = 0;
            _testFrameCounter = 0;
            _framesSent = 0;
            double intervalMs = SendRateHz > 0 ? 1000.0 / SendRateHz : 50;
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
            if (!_enabled || !_connection.IsConnected || _frameBuilder == null)
                return;

            try
            {
                byte[] frame;
                if (TestMode)
                {
                    var snapshot = Diagnostics.BuildTestPattern(_testFrameCounter++);
                    frame = _frameBuilder.BuildFrameFromSnapshot(snapshot, FlagByte);
                }
                else
                {
                    frame = _frameBuilder.BuildFrame(_latestGameData, FlagByte);
                }

                _connection.Send(frame);
                LastFrameSent = frame;
                _framesSent++;
                Diagnostics.RecordFrame(frame);

                // Periodically send telemetry mode frame to keep wheel in multi-channel mode
                if (SendTelemetryMode && (_modeCounter++ % 10 == 0))
                    _connection.Send(BuildTelemetryModeFrame());
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[Moza] Telemetry send error: {ex.Message}");
            }
        }

        private byte[] BuildTelemetryModeFrame()
        {
            // 7E 04 40 17 28 02 01 00 [checksum]
            // N=4: cmdId(2) + data(2) bytes after [7E N group device]
            var frame = new List<byte>
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
    }
}
