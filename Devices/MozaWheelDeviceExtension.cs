using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Controls;
using GameReaderCommon;
using Newtonsoft.Json.Linq;
using SimHub.Plugins;
using SimHub.Plugins.Devices;
using SimHub.Plugins.Devices.DeviceExtensions;
using SimHub.Plugins.OutputPlugins.GraphicalDash.LedModules;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// SimHub device extension for MOZA wheels.
    /// Injects a fake LED device driver so SimHub's effects UI works,
    /// then bridges computed LED colors to MOZA hardware via the plugin's serial protocol.
    /// Also provides per-game profile persistence through GetSettings()/SetSettings().
    /// </summary>
    internal class MozaWheelDeviceExtension : DeviceExtension
    {
        private MozaWheelExtensionSettings _settings = new MozaWheelExtensionSettings();
        private MozaLedDeviceManager? _ledDriver;
        private bool _driverInjected;
        private bool _buttonsCountSet;

        public override string ExtentionTabTitle => "MOZA Wheel";

        public override void Init(PluginManager pluginManager)
        {
            // Injection is deferred to DataUpdate() — calling it here would run before
            // LedModuleDevice.SetSettings(), causing a KeyNotFoundException in that call.

            var plugin = MozaPlugin.Instance;
            if (plugin != null)
                plugin.DeviceExtensionActive = true;

            // Always register — the delegate handles null Instance safely via ?.
            pluginManager.AttachDelegate(
                LinkedDevice.DeviceDescriptor.Name + "_MozaWheelActive",
                this.GetType(),
                () => MozaPlugin.Instance?.Data?.IsBaseConnected ?? false);
        }

        /// <summary>
        /// Find the LedModuleDevice sub-device and replace its DeviceDriver
        /// with our MozaLedDeviceManager that always reports connected.
        /// This enables SimHub's LED effects configuration UI.
        /// </summary>
        private void InjectLedDriver()
        {
            if (_driverInjected) return;

            try
            {
                foreach (var instance in LinkedDevice.GetInstances())
                {
                    if (instance is LedModuleDevice lmd && lmd.ledModuleSettings != null)
                    {
                        _ledDriver = new MozaLedDeviceManager();
                        _ledDriver.LedModuleSettings = lmd.ledModuleSettings;

                        // DeviceDriver setter is protected — use reflection
                        var prop = typeof(LedModuleSettings).GetProperty(
                            "DeviceDriver",
                            BindingFlags.Public | BindingFlags.Instance);

                        if (prop?.GetSetMethod(nonPublic: true) != null)
                        {
                            prop.GetSetMethod(nonPublic: true)!.Invoke(lmd.ledModuleSettings, new object[] { _ledDriver });
                            _driverInjected = true;

                            // Expose button LEDs for new-protocol wheels
                            var plugin = MozaPlugin.Instance;
                            if (plugin?.IsNewWheelDetected == true)
                            {
                                lmd.ledModuleSettings.ButtonsCount = MozaDeviceConstants.ButtonLedCount;
                                _buttonsCountSet = true;
                            }

                            if (plugin != null)
                                plugin.DeviceExtensionActive = true;

                            SimHub.Logging.Current.Info("[Moza] Injected virtual LED driver — effects UI should be available");
                        }
                        else
                        {
                            SimHub.Logging.Current.Warn("[Moza] Could not find DeviceDriver setter on LedModuleSettings");
                        }
                        return;
                    }
                }
                SimHub.Logging.Current.Info("[Moza] No LedModuleDevice found on device instance");
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"[Moza] Error injecting LED driver: {ex.Message}");
            }
        }

        public override void End(PluginManager pluginManager)
        {
            pluginManager.DetachDelegate(
                LinkedDevice.DeviceDescriptor.Name + "_MozaWheelActive",
                this.GetType());

            var plugin = MozaPlugin.Instance;
            if (plugin != null)
            {
                plugin.DeviceExtensionActive = false;
                SimHub.Logging.Current.Info("[Moza] Device extension ended");
            }
        }

        public override void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            // LED forwarding happens in MozaLedDeviceManager.Display() —
            // SimHub calls it directly as part of its LED pipeline.
            // Inject here (not Init) so LedModuleDevice.SetSettings() has already run.
            if (!_driverInjected)
                InjectLedDriver();

            // Notify SimHub when detection state changes so it resumes/pauses Display() calls
            _ledDriver?.UpdateConnectionState();

            // Set ButtonsCount once wheel detection completes (may happen after injection)
            if (_driverInjected && !_buttonsCountSet && MozaPlugin.Instance?.IsNewWheelDetected == true)
            {
                foreach (var instance in LinkedDevice.GetInstances())
                {
                    if (instance is LedModuleDevice lmd && lmd.ledModuleSettings != null)
                    {
                        lmd.ledModuleSettings.ButtonsCount = MozaDeviceConstants.ButtonLedCount;
                        _buttonsCountSet = true;
                        SimHub.Logging.Current.Info("[Moza] Set ButtonsCount=14 for new-protocol wheel");
                        break;
                    }
                }
            }
        }

        public override void LoadDefaultSettings()
        {
            _settings = new MozaWheelExtensionSettings();

            var plugin = MozaPlugin.Instance;
            if (plugin != null)
                _settings.CaptureFromCurrent(plugin.Settings, plugin.Data);
        }

        public override JToken GetSettings()
        {
            var plugin = MozaPlugin.Instance;
            if (plugin != null)
                _settings.CaptureFromCurrent(plugin.Settings, plugin.Data);

            return JToken.FromObject(_settings);
        }

        public override void SetSettings(JToken settings, bool isDefault)
        {
            _settings = settings.ToObject<MozaWheelExtensionSettings>() ?? new MozaWheelExtensionSettings();

            if (!isDefault)
            {
                var plugin = MozaPlugin.Instance;
                if (plugin != null)
                    plugin.ApplyWheelExtensionSettings(_settings);
            }
        }

        public override Control CreateSettingControl()
        {
            return new MozaWheelSettingsControl();
        }

        public override IEnumerable<DynamicButtonAction> GetDynamicButtonActions()
        {
            yield break;
        }
    }
}
