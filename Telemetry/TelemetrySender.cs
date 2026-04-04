using System;
using MozaPlugin.Protocol;


namespace MozaPlugin
{
    /// <summary>
    /// Sends SimHub game telemetry (RPM, flags) to Moza wheel/dashboard LEDs.
    /// Mirrors the logic from foxblat/simapi_handler.py.
    ///
    /// The Moza protocol uses a 10-bit bitmask where each bit corresponds to one
    /// RPM LED on the wheel/dashboard. Bit 15 controls the flag LEDs on the dashboard.
    /// The bitmask is sent via:
    ///   - dash-send-telemetry: write group 65, id [253, 222], 4-byte int
    ///   - wheel-send-rpm-telemetry: write group 63, id [26, 0], 2-byte array [low, high]
    ///   - wheel-old-send-telemetry: write group 65, id [253, 222], 4-byte int (ES wheels)
    ///
    /// New-protocol wheels also require color setup via wheel-telemetry-rpm-colors
    /// before the LEDs will respond.
    /// </summary>
    public class TelemetrySender
    {
        private readonly MozaSerialConnection _connection;
        private readonly MozaPluginSettings _settings;

        private const int LedCount = 10;
        private const int BlinkThreshold = 95;
        private const int FlagBit = 15; // Bit 15 in dash-send-telemetry activates flag LEDs

        // Blink state
        private bool _blinkState = true;
        private int _blinkCounter;
        private const int BlinkInterval = 4; // Toggle every N frames

        // Last sent state (avoid redundant writes)
        private int _lastBitmask = -1;

        private DateTime _lastSendTime = DateTime.MinValue;

        // Flag state
        private RaceFlag _lastFlag = RaceFlag.None;
        private bool _flagPhase;            // alternating phase for animated flags
        private DateTime _lastFlagToggle = DateTime.MinValue;

        // Target devices
        private bool _dashEnabled;
        private bool _wheelEnabled;
        private bool _wheelESProtocol;
        private bool _wheelColorsSent;

        // Default LED colors: green(1-3), red(4-7), blue(8-10)
        private static readonly byte[][] DefaultColors = new byte[][]
        {
            new byte[] { 0, 255, 0 },
            new byte[] { 0, 255, 0 },
            new byte[] { 0, 255, 0 },
            new byte[] { 255, 0, 0 },
            new byte[] { 255, 0, 0 },
            new byte[] { 255, 0, 0 },
            new byte[] { 255, 0, 0 },
            new byte[] { 0, 0, 255 },
            new byte[] { 0, 0, 255 },
            new byte[] { 0, 0, 255 },
        };

        private MozaDeviceManager _deviceManager;

        public bool DashEnabled { get => _dashEnabled; set => _dashEnabled = value; }
        public bool WheelEnabled { get => _wheelEnabled; set { _wheelEnabled = value; _wheelColorsSent = false; _ledsAwake = false; } }
        public bool WheelESProtocol { get => _wheelESProtocol; set { _wheelESProtocol = value; _wheelColorsSent = false; } }

        public TelemetrySender(MozaSerialConnection connection, MozaDeviceManager deviceManager, MozaPluginSettings settings)
        {
            _connection = connection;
            _deviceManager = deviceManager;
            _settings = settings;
        }

        /// <summary>
        /// Process a frame of game data and send RPM LED telemetry to the wheel.
        /// Call this from DataUpdate() on every frame.
        /// </summary>
        private int _debugCounter;
        private bool _gameWasRunning;
        private bool _ledsAwake;

        public void ProcessGameData(double currentRpm, double maxRpm, double idleRpm, bool gameRunning,
            RaceFlag activeFlag = RaceFlag.None, double shiftLight1 = 0, double shiftLight2 = 0, int redLineReached = 0)
        {
            if (!_connection.IsConnected)
                return;

            if (!_dashEnabled && !_wheelEnabled)
                return;

            if (!gameRunning)
            {
                if (_gameWasRunning)
                {
                    _gameWasRunning = false;
                    _ledsAwake = false;
                    ClearLeds();
                }
                return;
            }

            // Wake up LEDs when game becomes active AND wheel is enabled.
            // Some wheels (especially ES) don't respond to telemetry until
            // they receive a non-zero bitmask first.
            // We track _ledsAwake separately from _gameWasRunning because
            // the game might be detected before the wheel is.
            _gameWasRunning = true;
            if (!_ledsAwake && _wheelEnabled)
            {
                _ledsAwake = true;
                WakeUpLeds();
            }

            int rpmPercent = CalculateRpmPercent(currentRpm, maxRpm, idleRpm);

            // Compute bitmasks using each device's configured thresholds
            int dashBitmask = _dashEnabled
                ? (_settings.DashRpmMode == 2
                    ? CalculateSimHubBitmask(shiftLight1, shiftLight2, redLineReached)
                    : CalculateBitmask(currentRpm, rpmPercent, _settings.DashRpmMode, _settings.DashRpmTimingsPercent, _settings.DashRpmTimingsRpm))
                : 0;
            int wheelBitmask = _wheelEnabled
                ? (_settings.RpmMode == 2
                    ? CalculateSimHubBitmask(shiftLight1, shiftLight2, redLineReached)
                    : CalculateBitmask(currentRpm, rpmPercent, _settings.RpmMode, _settings.RpmTimingsPercent, _settings.RpmTimingsRpm))
                : 0;

            _debugCounter++;
            if (_debugCounter <= 5 || (_debugCounter % 300 == 0))
            {
                SimHub.Logging.Current.Info(
                    $"[Moza] RPM: {currentRpm:F0}/{maxRpm:F0} idle={idleRpm:F0} " +
                    $"pct={rpmPercent} dashMask=0x{dashBitmask:X3} wheelMask=0x{wheelBitmask:X3} " +
                    $"dash={_dashEnabled} wheel={_wheelEnabled} esProto={_wheelESProtocol} " +
                    $"wheelId={_deviceManager.WheelDeviceId}");
            }

            // Blink at high RPM; in SimHub mode use redLineReached instead of hardcoded threshold
            bool inSimHubMode = (_wheelEnabled && _settings.RpmMode == 2)
                             || (_dashEnabled && _settings.DashRpmMode == 2);
            bool shouldBlink = inSimHubMode ? (redLineReached != 0) : (rpmPercent >= BlinkThreshold);
            if (shouldBlink)
            {
                _blinkCounter++;
                if (_blinkCounter >= BlinkInterval)
                {
                    _blinkCounter = 0;
                    _blinkState = !_blinkState;
                }
                if (!_blinkState)
                {
                    dashBitmask = 0;
                    wheelBitmask = 0;
                }
            }
            else
            {
                _blinkState = true;
                _blinkCounter = 0;
            }

            // Flag LEDs: set bit 15 and update flag colors when flag changes
            if (_dashEnabled && activeFlag != RaceFlag.None)
            {
                bool animated = activeFlag == RaceFlag.Checkered || activeFlag == RaceFlag.Black;
                bool flagChanged = activeFlag != _lastFlag;
                bool phaseToggled = false;

                if (animated)
                {
                    var now2 = DateTime.UtcNow;
                    if (flagChanged || (now2 - _lastFlagToggle).TotalSeconds >= 1.0)
                    {
                        if (!flagChanged) _flagPhase = !_flagPhase;
                        else _flagPhase = false;
                        _lastFlagToggle = now2;
                        phaseToggled = true;
                    }
                }

                if (flagChanged || phaseToggled)
                {
                    _lastFlag = activeFlag;
                    SendFlagColors(activeFlag);
                }
                dashBitmask |= (1 << FlagBit);
            }
            else if (_lastFlag != RaceFlag.None)
            {
                _lastFlag = RaceFlag.None;
                // Bit 15 not set = flag LEDs off
            }

            // Send if changed or as keepalive (at least once per second)
            int combinedBitmask = dashBitmask | wheelBitmask;
            var now = DateTime.UtcNow;
            if (combinedBitmask != _lastBitmask || (now - _lastSendTime).TotalSeconds >= 1.0)
            {
                _lastBitmask = combinedBitmask;
                _lastSendTime = now;
                SendBitmasks(dashBitmask, wheelBitmask);
            }
        }

        public void ClearLeds()
        {
            _lastBitmask = 0;
            _lastFlag = RaceFlag.None;
            _flagPhase = false;
            _lastFlagToggle = DateTime.MinValue;
            SendBitmasks(0, 0);
        }

        /// <summary>
        /// Send an arbitrary bitmask for LED testing. Bypasses game telemetry logic.
        /// </summary>
        public void SendTestBitmask(int bitmask)
        {
            if (!_connection.IsConnected) return;
            _lastBitmask = bitmask;
            _lastSendTime = DateTime.UtcNow;
            SendBitmasks(bitmask, bitmask);
        }

        /// <summary>
        /// Flash all LEDs on then off to wake up the wheel's telemetry mode.
        /// Some wheels (especially ES) don't respond to telemetry until they
        /// receive a non-zero bitmask first.
        /// </summary>
        private void WakeUpLeds()
        {
            SimHub.Logging.Current.Info("[Moza] Waking up LEDs (all on -> off)");
            int fullBitmask = 0x3FF; // All 10 LEDs on
            SendBitmasks(fullBitmask, fullBitmask);
            SendBitmasks(0, 0);
            // Reset so the next real telemetry update gets sent
            _lastBitmask = -1;
        }

        private int CalculateRpmPercent(double rpm, double maxRpm, double idleRpm)
        {
            if (maxRpm <= idleRpm)
                return 0;

            double range = maxRpm - idleRpm;
            int percent = (int)(((rpm - idleRpm) / range) * 100.0);
            return Math.Max(0, Math.Min(100, percent));
        }

        private int CalculateBitmask(double currentRpm, int rpmPercent, int mode, int[] percentThresholds, int[] rpmThresholds)
        {
            int bitmask = 0;
            if (mode == 1) // RPM mode: compare against absolute RPM thresholds
            {
                for (int i = 0; i < LedCount; i++)
                {
                    if (currentRpm >= rpmThresholds[i])
                        bitmask |= (1 << i);
                }
            }
            else // Percent mode
            {
                for (int i = 0; i < LedCount; i++)
                {
                    if (rpmPercent >= percentThresholds[i])
                        bitmask |= (1 << i);
                }
            }
            return bitmask;
        }

        // SimHub mode: map 3 shift zones onto 10 LEDs (3 + 4 + 3).
        // shiftLight1/2 are 0→1 progress values from SimHub CarSettings.
        private int CalculateSimHubBitmask(double shiftLight1, double shiftLight2, int redLineReached)
        {
            int lit = (int)(Math.Min(1.0, Math.Max(0.0, shiftLight1)) * 3)
                    + (int)(Math.Min(1.0, Math.Max(0.0, shiftLight2)) * 4)
                    + (redLineReached != 0 ? 3 : 0);
            lit = Math.Min(lit, LedCount);
            return lit == 0 ? 0 : (1 << lit) - 1;
        }

        private int _sendCount;

        private void SendBitmasks(int dashBitmask, int wheelBitmask)
        {
            _sendCount++;
            if (_sendCount <= 3)
                SimHub.Logging.Current.Info($"[Moza] SendBitmask #{_sendCount}: dash=0x{dashBitmask:X3} wheel=0x{wheelBitmask:X3} es={_wheelESProtocol}");

            if (_dashEnabled)
                SendDashTelemetry(dashBitmask);

            if (_wheelEnabled)
            {
                if (_wheelESProtocol)
                    SendWheelESTelemetry(wheelBitmask);
                else
                    SendWheelNewTelemetry(wheelBitmask);
            }
        }

        /// <summary>
        /// Dashboard: write group 65, device main(18), id [253, 222], 4-byte int bitmask.
        /// Python uses get_device_id("dash") which returns device-ids["main"]=18 when
        /// the dash is connected through the base (not a separate USB device).
        /// </summary>
        private void SendDashTelemetry(int bitmask)
        {
            var cmd = MozaCommandDatabase.Get("dash-send-telemetry");
            if (cmd == null) return;

            var payload = new byte[4];
            payload[0] = (byte)((bitmask >> 24) & 0xFF);
            payload[1] = (byte)((bitmask >> 16) & 0xFF);
            payload[2] = (byte)((bitmask >> 8) & 0xFF);
            payload[3] = (byte)(bitmask & 0xFF);

            var msg = cmd.BuildWriteMessage(MozaProtocol.DeviceMain, payload);
            if (msg != null)
                _connection.Send(msg);
        }

        /// <summary>
        /// ES wheel: same as dash, write group 65, id [253, 222], 4-byte int.
        /// </summary>
        private void SendWheelESTelemetry(int bitmask)
        {
            var cmd = MozaCommandDatabase.Get("wheel-old-send-telemetry");
            if (cmd == null) return;

            var payload = new byte[4];
            payload[0] = (byte)((bitmask >> 24) & 0xFF);
            payload[1] = (byte)((bitmask >> 16) & 0xFF);
            payload[2] = (byte)((bitmask >> 8) & 0xFF);
            payload[3] = (byte)(bitmask & 0xFF);

            var msg = cmd.BuildWriteMessage(_deviceManager.WheelDeviceId, payload);
            if (_sendCount <= 3 && msg != null)
                SimHub.Logging.Current.Info($"[Moza] OldTelemetry: devId={_deviceManager.WheelDeviceId} msg={BitConverter.ToString(msg)}");
            if (msg != null)
                _connection.Send(msg);
        }

        /// <summary>
        /// New wheel: write group 63, device wheel(23), id [26, 0], 2-byte array [low, high].
        /// Requires color setup first via wheel-telemetry-rpm-colors.
        /// </summary>
        private void SendWheelNewTelemetry(int bitmask)
        {
            if (!_wheelColorsSent)
            {
                SetupWheelColors();
                _wheelColorsSent = true;
            }

            var cmd = MozaCommandDatabase.Get("wheel-send-rpm-telemetry");
            if (cmd == null) return;

            var payload = new byte[] { (byte)(bitmask & 0xFF), (byte)((bitmask >> 8) & 0xFF) };
            var msg = cmd.BuildWriteMessage(_deviceManager.WheelDeviceId, payload);
            if (msg != null)
                _connection.Send(msg);
        }

        /// <summary>
        /// Send LED color configuration to new-protocol wheels.
        /// Format: [index, R, G, B, index, R, G, B, ...] in two 20-byte chunks (5 LEDs each).
        /// </summary>
        private void SetupWheelColors()
        {
            var cmd = MozaCommandDatabase.Get("wheel-telemetry-rpm-colors");
            if (cmd == null) return;

            // Build color data: [index, R, G, B] * 10 LEDs = 40 bytes total
            var colorData = new byte[40];
            for (int i = 0; i < 10; i++)
            {
                int offset = i * 4;
                colorData[offset] = (byte)i;
                colorData[offset + 1] = DefaultColors[i][0];
                colorData[offset + 2] = DefaultColors[i][1];
                colorData[offset + 3] = DefaultColors[i][2];
            }

            // Send in two 20-byte chunks
            var chunk1 = new byte[20];
            var chunk2 = new byte[20];
            Array.Copy(colorData, 0, chunk1, 0, 20);
            Array.Copy(colorData, 20, chunk2, 0, 20);

            var msg1 = cmd.BuildWriteMessage(_deviceManager.WheelDeviceId, chunk1);
            var msg2 = cmd.BuildWriteMessage(_deviceManager.WheelDeviceId, chunk2);
            if (msg1 != null)
                _connection.Send(msg1);
            if (msg2 != null)
                _connection.Send(msg2);

            SimHub.Logging.Current.Info("[Moza] Sent wheel LED color configuration");
        }

        /// <summary>
        /// Write flag colors to all 6 dashboard flag LEDs via dash-flag-colors (18-byte bulk write).
        /// Checkered and black flags use alternating patterns that swap with _flagPhase.
        /// </summary>
        private void SendFlagColors(RaceFlag flag)
        {
            var payload = new byte[18]; // 6 LEDs × 3 bytes (R,G,B)

            if (flag == RaceFlag.Checkered)
            {
                // Odd LEDs white, even off — alternates every second
                for (int i = 0; i < 6; i++)
                {
                    bool lit = _flagPhase ? (i % 2 == 0) : (i % 2 != 0);
                    if (lit) { payload[i * 3] = 255; payload[i * 3 + 1] = 255; payload[i * 3 + 2] = 255; }
                }
            }
            else if (flag == RaceFlag.Black)
            {
                // LEDs 1-3 white, 4-6 off — alternates every second
                for (int i = 0; i < 6; i++)
                {
                    bool lit = _flagPhase ? (i >= 3) : (i < 3);
                    if (lit) { payload[i * 3] = 255; payload[i * 3 + 1] = 255; payload[i * 3 + 2] = 255; }
                }
            }
            else
            {
                var rgb = GetFlagColor(flag);
                for (int i = 0; i < 6; i++)
                {
                    payload[i * 3] = rgb[0];
                    payload[i * 3 + 1] = rgb[1];
                    payload[i * 3 + 2] = rgb[2];
                }
            }

            _deviceManager.WriteArray("dash-flag-colors", payload);
        }

        private static byte[] GetFlagColor(RaceFlag flag)
        {
            switch (flag)
            {
                case RaceFlag.Yellow:  return new byte[] { 255, 255, 0 };
                case RaceFlag.Blue:    return new byte[] { 0, 0, 255 };
                case RaceFlag.Green:   return new byte[] { 0, 255, 0 };
                case RaceFlag.White:   return new byte[] { 255, 255, 255 };
                case RaceFlag.Orange:  return new byte[] { 255, 165, 0 };
                default:               return new byte[] { 0, 0, 0 };
            }
        }
    }

    /// <summary>
    /// Race flag types matching SimHub's StatusDataBase flag properties.
    /// Ordered by priority (highest priority first for when multiple flags are active).
    /// </summary>
    public enum RaceFlag
    {
        None,
        Green,
        White,
        Blue,
        Yellow,
        Orange,
        Black,
        Checkered,
    }
}
