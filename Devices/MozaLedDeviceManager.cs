using System;
using System.Drawing;
using System.Linq;
using BA63Driver.Interfaces;
using BA63Driver.Mapper;
using SerialDash;
using SimHub.Plugins.OutputPlugins.GraphicalDash.LedModules;
using SimHub.Plugins.OutputPlugins.GraphicalDash.PSE;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// A virtual ILedDeviceManager that always reports as connected.
    /// SimHub's effects UI requires a connected device driver to enable LED configuration.
    /// This implementation captures the computed LED colors from Display() and forwards them
    /// to MOZA hardware via the plugin's serial protocol.
    /// </summary>
    internal class MozaLedDeviceManager : ILedDeviceManager
    {
        private Color[]? _lastLeds;
        private Color[]? _lastButtons;
        private LedDeviceState _lastState = new LedDeviceState(
            Array.Empty<Color>(), Array.Empty<Color>(), Array.Empty<Color>(),
            Array.Empty<Color>(), Array.Empty<Color>(), 1.0, 1.0, 1.0, 1.0);
        private double _lastBrightness = -1;
        private double _lastButtonsBrightness = -1;

        // Per-component bitmask tracking (avoid redundant bitmask sends)
        private int _lastRpmBitmask = -1;
        private int _lastButtonBitmask = -1;

        // Keepalive timer
        private DateTime _lastSendTime = DateTime.MinValue;
        private const double KeepaliveIntervalSeconds = 1.0;

        // ES wheel wake-up
        private bool _ledsAwake;

        /// <summary>
        /// Expected wheel model prefix for this device instance.
        /// Null = unknown (don't connect). Empty string = generic fallback (any wheel).
        /// Specific prefix (e.g. "CSP") = only connect when that model is detected.
        /// </summary>
        public string? ExpectedModelPrefix { get; set; }

        public LedModuleSettings LedModuleSettings { get; set; } = null!;

        public LedDeviceState LastState => _lastState;

        private bool _wasConnected;

        public event EventHandler? BeforeDisplay;
        public event EventHandler? AfterDisplay;
        public event EventHandler? OnConnect;
#pragma warning disable CS0067 // Required by ILedDeviceManager interface
        public event EventHandler? OnError;
#pragma warning restore CS0067
        public event EventHandler? OnDisconnect;

        /// <summary>
        /// Check current detection state and fire OnConnect/OnDisconnect if it changed.
        /// Called from device extension's DataUpdate() every frame.
        /// </summary>
        internal void UpdateConnectionState()
        {
            bool connected = IsConnected();
            if (connected == _wasConnected) return;
            _wasConnected = connected;

            if (connected)
            {
                OnConnect?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                // Reset cached state so everything re-initializes on reconnect
                _lastLeds = null;
                _lastButtons = null;
                _lastRpmBitmask = -1;
                _lastButtonBitmask = -1;
                _lastBrightness = -1;
                _lastButtonsBrightness = -1;
                _ledsAwake = false;
                OnDisconnect?.Invoke(this, EventArgs.Empty);
            }
        }

        public bool IsConnected()
        {
            if (ExpectedModelPrefix == null)
                return false;

            var p = MozaPlugin.Instance;
            if (p == null)
                return false;

            // Old-protocol device — only match old-protocol wheels
            if (ExpectedModelPrefix == MozaDeviceConstants.OldProtocolMarker)
                return p.IsOldWheelDetected;

            // All other prefixes require a new-protocol wheel
            if (!p.IsNewWheelDetected)
                return false;

            // Empty prefix = generic fallback, matches any new-protocol wheel
            // UNLESS a model-specific device extension is active for this wheel
            if (ExpectedModelPrefix.Length == 0)
                return !p.IsModelSpecificExtensionActive(p.Data.WheelModelName);

            // Specific model — match against detected wheel's firmware model name
            var modelName = p.Data.WheelModelName;
            if (string.IsNullOrEmpty(modelName))
                return false;

            return modelName.StartsWith(ExpectedModelPrefix, StringComparison.OrdinalIgnoreCase);
        }

        public string GetSerialNumber() => "MOZA-VIRTUAL";

        public string GetFirmwareVersion() =>
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

        public object GetDriverInstance() => this;

        public void Close() { }

        public void ResetDetection() { }

        public void SerialPortCanBeScanned(object sender, SerialDashController.ScanArgs e) { }

        public IPhysicalMapper GetPhysicalMapper() => new NeutralLedsMapper();

        public ILedDriverBase? GetLedDriver() => null;

        public void Display(
            Func<Color[]> leds,
            Func<Color[]> buttons,
            Func<Color[]> encoders,
            Func<Color[]> matrix,
            Func<Color[]> rawState,
            bool forceRefresh,
            Func<object>? extraData = null,
            double rpmBrightness = 1.0,
            double buttonsBrightness = 1.0,
            double encodersBrightness = 1.0,
            double matrixBrightness = 1.0)
        {
            BeforeDisplay?.Invoke(this, EventArgs.Empty);

            try
            {
                var ledColors = leds?.Invoke() ?? Array.Empty<Color>();
                var buttonColors = buttons?.Invoke() ?? Array.Empty<Color>();
                var encoderColors = encoders?.Invoke() ?? Array.Empty<Color>();
                var matrixColors = matrix?.Invoke() ?? Array.Empty<Color>();
                var rawColors = rawState?.Invoke() ?? Array.Empty<Color>();

                _lastState = new LedDeviceState(
                    ledColors, buttonColors, encoderColors, matrixColors, rawColors,
                    rpmBrightness, buttonsBrightness, encodersBrightness, matrixBrightness);

                if (ledColors.Length == 0)
                    return;

                var plugin = MozaPlugin.Instance;
                if (plugin == null || !plugin.Data.IsBaseConnected)
                    return;

                bool isNewWheel = plugin.IsNewWheelDetected;
                bool isOldWheel = plugin.IsOldWheelDetected;
                if (!isNewWheel && !isOldWheel)
                    return;

                // ES wheel wake-up: flash all LEDs on then off to enter telemetry mode
                if (!_ledsAwake && isOldWheel)
                {
                    _ledsAwake = true;
                    plugin.DeviceManager.WriteSetting("wheel-old-send-telemetry", 0x3FF);
                    plugin.DeviceManager.WriteSetting("wheel-old-send-telemetry", 0);
                    SimHub.Logging.Current.Info("[Moza] ES wheel LED wake-up sent");
                }

                bool limitUpdates = plugin.Settings.LimitWheelUpdates;
                bool alwaysResendBitmask = plugin.Settings.AlwaysResendBitmask;
                bool anySent = false;

                // --- RPM LEDs ---
                bool rpmChanged = _lastLeds == null || !ledColors.SequenceEqual(_lastLeds);
                bool shouldSendRpm = rpmChanged || (!limitUpdates && forceRefresh);

                if (shouldSendRpm)
                {
                    _lastLeds = (Color[])ledColors.Clone();

                    int count = Math.Min(ledColors.Length, MozaDeviceConstants.RpmLedCount);

                    // Build bitmask: bit i set if LED i has any color
                    int bitmask = 0;
                    for (int i = 0; i < count; i++)
                    {
                        if (ledColors[i].R > 0 || ledColors[i].G > 0 || ledColors[i].B > 0)
                            bitmask |= (1 << i);
                    }

                    if (isNewWheel)
                    {
                        SendColorChunks(plugin, ledColors, count, "wheel-telemetry-rpm-colors");

                        if (alwaysResendBitmask || bitmask != _lastRpmBitmask)
                        {
                            _lastRpmBitmask = bitmask;
                            plugin.DeviceManager.WriteArray("wheel-send-rpm-telemetry",
                                new byte[] { (byte)(bitmask & 0xFF), (byte)((bitmask >> 8) & 0xFF) });
                        }
                        anySent = true;
                    }
                    else if (isOldWheel)
                    {
                        // ES wheels: can't set colors per-frame, just send bitmask
                        if (alwaysResendBitmask || bitmask != _lastRpmBitmask)
                        {
                            _lastRpmBitmask = bitmask;
                            plugin.DeviceManager.WriteSetting("wheel-old-send-telemetry", bitmask);
                            anySent = true;
                        }
                    }
                }

                // --- Button LEDs (new-protocol wheels only) ---
                // Gate on WheelModelInfo being known: sending with the fallback mapping
                // before the model-name response arrives would push wrong-index state that
                // the cache then treats as current, leaving the wheel misaligned until a
                // power cycle or forced color change.
                var modelInfo = plugin.WheelModelInfo;
                if (isNewWheel && buttonColors.Length > 0 && modelInfo != null)
                {
                    bool buttonsChanged = _lastButtons == null || !buttonColors.SequenceEqual(_lastButtons);
                    bool shouldSendButtons = buttonsChanged || (!limitUpdates && forceRefresh);

                    if (shouldSendButtons)
                    {
                        _lastButtons = (Color[])buttonColors.Clone();

                        int buttonCount = Math.Min(buttonColors.Length, modelInfo.ButtonLedCount);
                        var buttonMap = modelInfo.ButtonLedMap;

                        int buttonBitmask = 0;
                        for (int i = 0; i < buttonCount; i++)
                        {
                            int protocolIndex = buttonMap != null ? buttonMap[i] : i;
                            if (buttonColors[i].R > 0 || buttonColors[i].G > 0 || buttonColors[i].B > 0)
                                buttonBitmask |= (1 << protocolIndex);
                        }

                        SendColorChunks(plugin, buttonColors, buttonCount, "wheel-telemetry-button-colors", buttonMap);

                        if (alwaysResendBitmask || buttonBitmask != _lastButtonBitmask)
                        {
                            _lastButtonBitmask = buttonBitmask;
                            plugin.DeviceManager.WriteArray("wheel-send-buttons-telemetry",
                                new byte[] { (byte)(buttonBitmask & 0xFF), (byte)((buttonBitmask >> 8) & 0xFF) });
                        }
                        anySent = true;
                    }
                }

                // --- Brightness (existing change detection) ---
                if (rpmBrightness != _lastBrightness)
                {
                    _lastBrightness = rpmBrightness;
                    if (isNewWheel)
                        plugin.DeviceManager.WriteSetting("wheel-rpm-brightness", (int)(rpmBrightness * 100));
                    else if (isOldWheel)
                        plugin.DeviceManager.WriteSetting("wheel-old-rpm-brightness", (int)(rpmBrightness * 15));
                    anySent = true;
                }

                if (isNewWheel && buttonsBrightness != _lastButtonsBrightness)
                {
                    _lastButtonsBrightness = buttonsBrightness;
                    plugin.DeviceManager.WriteSetting("wheel-buttons-brightness", (int)(buttonsBrightness * 100));
                    anySent = true;
                }

                // --- Keepalive: resend last state periodically for ES wheel compat ---
                if (anySent)
                {
                    _lastSendTime = DateTime.UtcNow;
                }
                else if (plugin.Settings.WheelKeepalive && _lastLeds != null)
                {
                    var now = DateTime.UtcNow;
                    if ((now - _lastSendTime).TotalSeconds >= KeepaliveIntervalSeconds)
                    {
                        _lastSendTime = now;
                        ResendLastState(plugin, isNewWheel, isOldWheel);
                    }
                }
            }
            finally
            {
                AfterDisplay?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Resend the last known LED state as a keepalive.
        /// </summary>
        private void ResendLastState(MozaPlugin plugin, bool isNewWheel, bool isOldWheel)
        {
            if (_lastLeds == null) return;

            int count = Math.Min(_lastLeds.Length, MozaDeviceConstants.RpmLedCount);

            if (isNewWheel)
            {
                SendColorChunks(plugin, _lastLeds, count, "wheel-telemetry-rpm-colors");
                if (_lastRpmBitmask >= 0)
                    plugin.DeviceManager.WriteArray("wheel-send-rpm-telemetry",
                        new byte[] { (byte)(_lastRpmBitmask & 0xFF), (byte)((_lastRpmBitmask >> 8) & 0xFF) });
            }
            else if (isOldWheel)
            {
                if (_lastRpmBitmask >= 0)
                    plugin.DeviceManager.WriteSetting("wheel-old-send-telemetry", _lastRpmBitmask);
            }
        }

        /// <summary>
        /// Pack colors into 4-byte-per-LED format and send in 20-byte chunks.
        /// When <paramref name="indexMap"/> is provided, each entry maps the source array
        /// position to the protocol LED index (for non-contiguous button layouts).
        /// </summary>
        private static void SendColorChunks(MozaPlugin plugin, Color[] colors, int count,
            string command, int[]? indexMap = null)
        {
            int dataLen = count * 4;
            // Round up to next multiple of 20 for chunk alignment
            int bufferLen = ((dataLen + 19) / 20) * 20;
            var colorData = new byte[bufferLen];

            for (int i = 0; i < count; i++)
            {
                int offset = i * 4;
                colorData[offset] = (byte)(indexMap != null ? indexMap[i] : i);
                colorData[offset + 1] = colors[i].R;
                colorData[offset + 2] = colors[i].G;
                colorData[offset + 3] = colors[i].B;
            }

            // Fill padding entries with unused index 0xFF so firmware doesn't
            // interpret zero-padding as "set LED 0 to black" (causes button 0 flicker)
            for (int pos = dataLen; pos < bufferLen; pos += 4)
                colorData[pos] = 0xFF;

            for (int pos = 0; pos < bufferLen; pos += 20)
            {
                var chunk = new byte[20];
                Array.Copy(colorData, pos, chunk, 0, 20);
                plugin.DeviceManager.WriteArray(command, chunk);
            }
        }
    }
}
