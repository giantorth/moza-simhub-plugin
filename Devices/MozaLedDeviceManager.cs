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

        public LedModuleSettings LedModuleSettings { get; set; } = null!;

        public LedDeviceState LastState => _lastState;

        public event EventHandler? BeforeDisplay;
        public event EventHandler? AfterDisplay;
#pragma warning disable CS0067 // Required by ILedDeviceManager interface
        public event EventHandler? OnConnect;
        public event EventHandler? OnError;
        public event EventHandler? OnDisconnect;
#pragma warning restore CS0067

        public bool IsConnected() => true;

        public string GetSerialNumber() => "MOZA-VIRTUAL";

        public string GetFirmwareVersion() => "1.0";

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

                // Only send if colors changed
                if (!forceRefresh && _lastLeds != null && ledColors.SequenceEqual(_lastLeds))
                    return;

                _lastLeds = (Color[])ledColors.Clone();

                var plugin = MozaPlugin.Instance;
                if (plugin == null || !plugin.Data.IsBaseConnected)
                    return;

                bool isNewWheel = plugin.IsNewWheelDetected;
                bool isOldWheel = plugin.IsOldWheelDetected;
                if (!isNewWheel && !isOldWheel)
                    return;

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
                    plugin.DeviceManager.WriteArray("wheel-send-rpm-telemetry",
                        new byte[] { (byte)(bitmask & 0xFF), (byte)((bitmask >> 8) & 0xFF) });
                }
                else if (isOldWheel)
                {
                    // ES wheels: can't set colors per-frame, just send bitmask
                    plugin.DeviceManager.WriteSetting("wheel-old-send-telemetry", bitmask);
                }

                // Forward button colors for new-protocol wheels
                if (isNewWheel && buttonColors.Length > 0)
                {
                    if (forceRefresh || _lastButtons == null || !buttonColors.SequenceEqual(_lastButtons))
                    {
                        _lastButtons = (Color[])buttonColors.Clone();

                        int buttonCount = Math.Min(buttonColors.Length, MozaDeviceConstants.ButtonLedCount);

                        int buttonBitmask = 0;
                        for (int i = 0; i < buttonCount; i++)
                        {
                            if (buttonColors[i].R > 0 || buttonColors[i].G > 0 || buttonColors[i].B > 0)
                                buttonBitmask |= (1 << i);
                        }

                        SendColorChunks(plugin, buttonColors, buttonCount, "wheel-telemetry-button-colors");
                        plugin.DeviceManager.WriteArray("wheel-send-buttons-telemetry",
                            new byte[] { (byte)(buttonBitmask & 0xFF), (byte)((buttonBitmask >> 8) & 0xFF) });
                    }
                }

                // Forward brightness to wheel hardware when it changes
                if (rpmBrightness != _lastBrightness)
                {
                    _lastBrightness = rpmBrightness;
                    if (isNewWheel)
                        plugin.DeviceManager.WriteSetting("wheel-rpm-brightness", (int)(rpmBrightness * 100));
                    else if (isOldWheel)
                        plugin.DeviceManager.WriteSetting("wheel-old-rpm-brightness", (int)(rpmBrightness * 15));
                }

                if (isNewWheel && buttonsBrightness != _lastButtonsBrightness)
                {
                    _lastButtonsBrightness = buttonsBrightness;
                    plugin.DeviceManager.WriteSetting("wheel-buttons-brightness", (int)(buttonsBrightness * 100));
                }
            }
            finally
            {
                AfterDisplay?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Pack colors into 4-byte-per-LED format and send in 20-byte chunks.
        /// </summary>
        private static void SendColorChunks(MozaPlugin plugin, Color[] colors, int count, string command)
        {
            int dataLen = count * 4;
            // Round up to next multiple of 20 for chunk alignment
            int bufferLen = ((dataLen + 19) / 20) * 20;
            var colorData = new byte[bufferLen];

            for (int i = 0; i < count; i++)
            {
                int offset = i * 4;
                colorData[offset] = (byte)i;
                colorData[offset + 1] = colors[i].R;
                colorData[offset + 2] = colors[i].G;
                colorData[offset + 3] = colors[i].B;
            }

            for (int pos = 0; pos < bufferLen; pos += 20)
            {
                var chunk = new byte[20];
                Array.Copy(colorData, pos, chunk, 0, 20);
                plugin.DeviceManager.WriteArray(command, chunk);
            }
        }
    }
}
