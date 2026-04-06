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
        private LedDeviceState? _lastState;
        private double _lastBrightness = -1;
        private double _lastButtonsBrightness = -1;

        public LedModuleSettings LedModuleSettings { get; set; } = null!;

        public LedDeviceState LastState => _lastState!;

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

                int count = Math.Min(ledColors.Length, 10);

                // Build bitmask: bit i set if LED i has any color
                int bitmask = 0;
                for (int i = 0; i < count; i++)
                {
                    if (ledColors[i].R > 0 || ledColors[i].G > 0 || ledColors[i].B > 0)
                        bitmask |= (1 << i);
                }

                if (isNewWheel)
                {
                    // Send colors via bulk telemetry command (2 chunks of 20 bytes)
                    var colorData = new byte[40];
                    for (int i = 0; i < count; i++)
                    {
                        int offset = i * 4;
                        colorData[offset] = (byte)i;
                        colorData[offset + 1] = ledColors[i].R;
                        colorData[offset + 2] = ledColors[i].G;
                        colorData[offset + 3] = ledColors[i].B;
                    }

                    var chunk1 = new byte[20];
                    var chunk2 = new byte[20];
                    Array.Copy(colorData, 0, chunk1, 0, 20);
                    Array.Copy(colorData, 20, chunk2, 0, 20);
                    plugin.DeviceManager.WriteArray("wheel-telemetry-rpm-colors", chunk1);
                    plugin.DeviceManager.WriteArray("wheel-telemetry-rpm-colors", chunk2);

                    // Send bitmask to turn LEDs on
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

                        int buttonCount = Math.Min(buttonColors.Length, 14);

                        // Build bitmask for buttons
                        int buttonBitmask = 0;
                        for (int i = 0; i < buttonCount; i++)
                        {
                            if (buttonColors[i].R > 0 || buttonColors[i].G > 0 || buttonColors[i].B > 0)
                                buttonBitmask |= (1 << i);
                        }

                        // Send colors via bulk telemetry command (3 chunks of 20 bytes)
                        var colorData = new byte[60];
                        for (int i = 0; i < buttonCount; i++)
                        {
                            int offset = i * 4;
                            colorData[offset] = (byte)i;
                            colorData[offset + 1] = buttonColors[i].R;
                            colorData[offset + 2] = buttonColors[i].G;
                            colorData[offset + 3] = buttonColors[i].B;
                        }

                        var chunk1 = new byte[20];
                        var chunk2 = new byte[20];
                        var chunk3 = new byte[20];
                        Array.Copy(colorData, 0, chunk1, 0, 20);
                        Array.Copy(colorData, 20, chunk2, 0, 20);
                        Array.Copy(colorData, 40, chunk3, 0, 20);
                        plugin.DeviceManager.WriteArray("wheel-telemetry-button-colors", chunk1);
                        plugin.DeviceManager.WriteArray("wheel-telemetry-button-colors", chunk2);
                        plugin.DeviceManager.WriteArray("wheel-telemetry-button-colors", chunk3);

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
    }
}
