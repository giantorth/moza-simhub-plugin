using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Timers;
using System.Windows.Media;
using GameReaderCommon;
using Newtonsoft.Json.Linq;
using SimHub.Plugins;
using MozaPlugin.Devices;
using MozaPlugin.Protocol;
using MozaPlugin.Telemetry;
using Timer = System.Timers.Timer;

namespace MozaPlugin
{
    [PluginDescription("Configure MOZA Racing hardware and send SimHub game telemetry to wheel/dashboard RPM LEDs")]
    [PluginAuthor("giantorth")]
    [PluginName("MOZA Control")]
    public class MozaPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        internal static MozaPlugin? Instance { get; private set; }

        private MozaSerialConnection _connection = null!;
        private MozaData _data = null!;
        private MozaDeviceManager _deviceManager = null!;
        private MozaPluginSettings _settings = null!;
        private Timer _pollTimer = null!;
        private Timer _reconnectTimer = null!;
        private PluginManager _pluginManager = null!;
        private TelemetrySender _telemetrySender = null!;
        internal DashboardProfileStore DashProfileStore { get; } = new DashboardProfileStore();

        // Device detection state
        private bool _baseDetected;
        private bool _dashDetected;
        private bool _newWheelDetected;
        private bool _oldWheelDetected;
        private bool _handbrakeDetected;
        private bool _pedalsDetected;

        // Guard against concurrent/duplicate telemetry Start() dispatch
        private int _telemetryStartRequested;

        // Debounce disk writes during rapid slider changes
        private Timer? _saveDebounceTimer;

        private static readonly string[] StatusPollCommands = new[]
        {
            "base-mcu-temp", "base-mosfet-temp", "base-motor-temp",
            "base-state",
        };

        private static readonly string[] SettingsPollCommands = new[]
        {
            // Base
            "base-limit", "base-ffb-strength", "base-torque", "base-speed",
            "base-damper", "base-friction", "base-inertia", "base-spring",
            "base-protection", "base-natural-inertia",
            "base-speed-damping", "base-speed-damping-point",
            "base-soft-limit-stiffness", "base-soft-limit-retain",
            "base-ffb-reverse", "base-temp-strategy",
            "main-get-work-mode", "main-get-led-status",
            "main-get-damper-gain", "main-get-friction-gain",
            "main-get-inertia-gain", "main-get-spring-gain",
            "main-get-ble-mode",
            // FFB Equalizer
            "base-equalizer1", "base-equalizer2", "base-equalizer3",
            "base-equalizer4", "base-equalizer5", "base-equalizer6",
            // FFB Curve (Y outputs only; X breakpoints are fixed at 20/40/60/80)
            "base-ffb-curve-y1", "base-ffb-curve-y2", "base-ffb-curve-y3", "base-ffb-curve-y4", "base-ffb-curve-y5",
            // Wheel LED
            "wheel-telemetry-mode", "wheel-telemetry-idle-effect",
            "wheel-buttons-idle-effect",
            "wheel-rpm-brightness", "wheel-buttons-brightness", "wheel-flags-brightness",
            "wheel-idle-mode", "wheel-idle-timeout", "wheel-idle-speed",
            "wheel-idle-color",
            // Wheel paddle settings
            "wheel-paddles-mode", "wheel-clutch-point", "wheel-knob-mode", "wheel-stick-mode",
            // Wheel RPM colors
            "wheel-rpm-color1", "wheel-rpm-color2", "wheel-rpm-color3",
            "wheel-rpm-color4", "wheel-rpm-color5", "wheel-rpm-color6",
            "wheel-rpm-color7", "wheel-rpm-color8", "wheel-rpm-color9",
            "wheel-rpm-color10",
            // ES Wheel specific
            "wheel-rpm-indicator-mode", "wheel-get-rpm-display-mode",
            "wheel-old-rpm-brightness",
            "wheel-old-rpm-color1", "wheel-old-rpm-color2", "wheel-old-rpm-color3",
            "wheel-old-rpm-color4", "wheel-old-rpm-color5", "wheel-old-rpm-color6",
            "wheel-old-rpm-color7", "wheel-old-rpm-color8", "wheel-old-rpm-color9",
            "wheel-old-rpm-color10",
            // Dash LED
            "dash-rpm-indicator-mode", "dash-flags-indicator-mode",
            "dash-rpm-display-mode",
            "dash-rpm-brightness", "dash-flags-brightness",
            // Dash RPM colors
            "dash-rpm-color1", "dash-rpm-color2", "dash-rpm-color3",
            "dash-rpm-color4", "dash-rpm-color5", "dash-rpm-color6",
            "dash-rpm-color7", "dash-rpm-color8", "dash-rpm-color9",
            "dash-rpm-color10",
            // Dash flag colors
            "dash-flag-color1", "dash-flag-color2", "dash-flag-color3",
            "dash-flag-color4", "dash-flag-color5", "dash-flag-color6",
            // Handbrake
            "handbrake-direction", "handbrake-min", "handbrake-max",
            "handbrake-mode", "handbrake-button-threshold",
            "handbrake-y1", "handbrake-y2", "handbrake-y3", "handbrake-y4", "handbrake-y5",
            // Pedals settings
            "pedals-throttle-dir", "pedals-throttle-min", "pedals-throttle-max",
            "pedals-brake-dir", "pedals-brake-min", "pedals-brake-max", "pedals-brake-angle-ratio",
            "pedals-clutch-dir", "pedals-clutch-min", "pedals-clutch-max",
            "pedals-throttle-y1", "pedals-throttle-y2", "pedals-throttle-y3", "pedals-throttle-y4", "pedals-throttle-y5",
            "pedals-brake-y1", "pedals-brake-y2", "pedals-brake-y3", "pedals-brake-y4", "pedals-brake-y5",
            "pedals-clutch-y1", "pedals-clutch-y2", "pedals-clutch-y3", "pedals-clutch-y4", "pedals-clutch-y5",
        };

        public PluginManager PluginManager { set => _pluginManager = value; }
        public ImageSource? PictureIcon => null;
        public string LeftMenuTitle => "MOZA";

        internal bool ConnectionEnabled => _settings?.ConnectionEnabled ?? true;

        internal MozaData Data => _data;
        internal MozaDeviceManager DeviceManager => _deviceManager;
        internal MozaPluginSettings Settings => _settings;
        internal bool IsNewWheelDetected => _newWheelDetected;
        internal bool IsOldWheelDetected => _oldWheelDetected;
        internal Devices.WheelModelInfo? WheelModelInfo { get; private set; }
        /// <summary>
        /// When true, the device extension owns wheel LED settings via its own profile system.
        /// Plugin profile application skips wheel settings to avoid conflicts.
        /// </summary>
        private volatile bool _deviceExtensionActive;
        internal bool DeviceExtensionActive
        {
            get => _deviceExtensionActive;
            set => _deviceExtensionActive = value;
        }

        private volatile bool _dashDeviceExtensionActive;
        internal bool DashDeviceExtensionActive
        {
            get => _dashDeviceExtensionActive;
            set => _dashDeviceExtensionActive = value;
        }

        /// <summary>
        /// Tracks model prefixes with an active (loaded) device extension in this SimHub session.
        /// Used by the generic fallback device to yield when a model-specific device is active.
        /// </summary>
        // Copy-on-write for thread safety: reads get a consistent snapshot,
        // mutations (rare — only on device extension init/end) create a new set.
        private volatile HashSet<string> _activeModelPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal void RegisterActiveModelPrefix(string prefix)
        {
            if (!string.IsNullOrEmpty(prefix) && prefix != MozaDeviceConstants.OldProtocolMarker)
            {
                var newSet = new HashSet<string>(_activeModelPrefixes, StringComparer.OrdinalIgnoreCase);
                newSet.Add(prefix);
                _activeModelPrefixes = newSet;
            }
        }

        internal void UnregisterActiveModelPrefix(string prefix)
        {
            if (!string.IsNullOrEmpty(prefix))
            {
                var newSet = new HashSet<string>(_activeModelPrefixes, StringComparer.OrdinalIgnoreCase);
                newSet.Remove(prefix);
                _activeModelPrefixes = newSet;
            }
        }

        /// <summary>
        /// Returns true if a model-specific device extension is active for the given wheel model.
        /// </summary>
        internal bool IsModelSpecificExtensionActive(string modelName)
        {
            if (string.IsNullOrEmpty(modelName) || _activeModelPrefixes.Count == 0)
                return false;

            foreach (var prefix in _activeModelPrefixes)
            {
                if (modelName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Set to true when a new device definition is deployed at runtime.
        /// The plugin settings panel shows a restart notice when this is true.
        /// </summary>
        internal volatile bool DeviceDefinitionDeployed;

        internal bool IsDashDetected => _dashDetected;
        internal bool IsHandbrakeDetected => _handbrakeDetected;
        internal bool IsPedalsDetected => _pedalsDetected;

        /// <summary>True if the wheel's internal Display sub-device responded to probe.</summary>
        internal bool IsDisplayDetected => _telemetrySender?.DisplayDetected ?? false;

        /// <summary>Display sub-device model name (e.g. "Display"), or empty.</summary>
        internal string DisplayModelName => _telemetrySender?.DisplayModelName ?? "";
        internal MozaProfileStore ProfileStore => _settings?.ProfileStore!;

        public void Init(PluginManager pluginManager)
        {
            Instance = this;
            _pluginManager = pluginManager;
            _data = new MozaData();
            _settings = this.ReadCommonSettings<MozaPluginSettings>("MozaPluginSettings", () => new MozaPluginSettings());

            // Null-guard for upgraded settings missing ProfileStore
            if (_settings.ProfileStore == null)
                _settings.ProfileStore = new MozaProfileStore();

            // Restore blink colors from settings (write-only, can't be polled from device)
            MozaProfile.UnpackColorsInto(_settings.WheelRpmBlinkColors, _data.WheelRpmBlinkColors);
            MozaProfile.UnpackColorsInto(_settings.DashRpmBlinkColors, _data.DashRpmBlinkColors);

            SimHub.Logging.Current.Info("[Moza] Initializing plugin");

            MozaDeviceConstants.InitializeRegistry();

            // Read SimHub's global temperature unit preference (set at first launch)
            var tempUnit = pluginManager.GetPropertyValue("DataCorePlugin.GameData.TemperatureUnit");
            _data.UseFahrenheit = string.Equals(tempUnit as string, "Fahrenheit", StringComparison.OrdinalIgnoreCase);
            SimHub.Logging.Current.Info($"[Moza] Temperature unit: {(_data.UseFahrenheit ? "Fahrenheit" : "Celsius")}");

            // Initialize the native profile system (detects current game, selects profile)
            InitProfileSystem();

            RegisterProperties(pluginManager);
            RegisterActions();

            _connection = new MozaSerialConnection();
            _connection.MessageReceived += OnMessageReceived;

            _deviceManager = new MozaDeviceManager(_connection);

            if (_settings.ConnectionEnabled)
                TryConnect();

            _pollTimer = new Timer(2000);
            _pollTimer.Elapsed += PollStatus;
            _pollTimer.AutoReset = true;
            _pollTimer.Start();

            _reconnectTimer = new Timer(5000);
            _reconnectTimer.Elapsed += (s, e) =>
            {
                if (!_connection.IsConnected)
                    TryConnect();
            };
            _reconnectTimer.AutoReset = true;
            if (_settings.ConnectionEnabled)
                _reconnectTimer.Start();

            _telemetrySender = new TelemetrySender(_connection);
            ApplyTelemetrySettings();
            // Don't start telemetry here — defer until wheel is detected.
            // The session open probe requires the wheel to be present and responsive.
            // StartTelemetryIfReady() is called from DetectDevices() when the wheel
            // is first detected, and from profile application callbacks.
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            _telemetrySender?.UpdateGameData(data.NewData);
        }

        public void End(PluginManager pluginManager)
        {
            Instance = null;
            SimHub.Logging.Current.Info("[Moza] Shutting down plugin");
            _saveDebounceTimer?.Stop();
            _saveDebounceTimer?.Dispose();
            _saveDebounceTimer = null;
            this.SaveCommonSettings("MozaPluginSettings", _settings);
            ClearLedsOnHardware();
            _telemetrySender?.Stop();
            _telemetrySender?.Dispose();
            _pollTimer?.Stop();
            _pollTimer?.Dispose();
            _reconnectTimer?.Stop();
            _reconnectTimer?.Dispose();
            _connection?.Dispose();
        }

        internal void SaveSettings()
        {
            _settings.ProfileStore?.CurrentProfile?.CaptureFromCurrent(_settings, _data);
            ScheduleSave();
        }

        private void PersistSettings()
        {
            ScheduleSave();
        }

        /// <summary>
        /// Debounce disk writes: restart a 500ms timer on each call.
        /// Prevents dozens of writes per second during rapid slider drags.
        /// </summary>
        private void ScheduleSave()
        {
            if (_saveDebounceTimer == null)
            {
                _saveDebounceTimer = new Timer(500) { AutoReset = false };
                _saveDebounceTimer.Elapsed += (s, e) =>
                    this.SaveCommonSettings("MozaPluginSettings", _settings);
            }
            _saveDebounceTimer.Stop();
            _saveDebounceTimer.Start();
        }

        internal void ClearSettings()
        {
            _settings = new MozaPluginSettings();
            this.SaveCommonSettings("MozaPluginSettings", _settings);
            InitProfileSystem();
        }

        internal void SetConnectionEnabled(bool enabled)
        {
            _settings.ConnectionEnabled = enabled;
            SaveSettings();

            if (enabled)
            {
                _reconnectTimer.Start();
                if (!_connection.IsConnected)
                    TryConnect();
                SimHub.Logging.Current.Info("[Moza] Connection enabled");
            }
            else
            {
                _reconnectTimer.Stop();
                ClearLedsOnHardware();
                _telemetrySender?.Stop();
                _connection?.Disconnect();
                _data.IsBaseConnected = false;
                _data.ClearWheelIdentity();
                _baseDetected = false;
                _data.BaseSettingsRead = false;
                _dashDetected = false;
                _newWheelDetected = false;
                _oldWheelDetected = false;
                WheelModelInfo = null;
                _handbrakeDetected = false;
                _pedalsDetected = false;
                _deviceManager.ResetWheelDetection();
                _telemetrySender.DetectedDeviceMask = 0;
                Interlocked.Exchange(ref _telemetryStartRequested, 0);
                SimHub.Logging.Current.Info("[Moza] Connection disabled");
            }
        }

        public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new SettingsControl(this);
        }

        private void RegisterProperties(PluginManager pluginManager)
        {
            this.AttachDelegate("Moza.BaseConnected", () => _data.IsBaseConnected);
            this.AttachDelegate("Moza.McuTemp", () => ConvertTemp(_data.McuTemp));
            this.AttachDelegate("Moza.MosfetTemp", () => ConvertTemp(_data.MosfetTemp));
            this.AttachDelegate("Moza.MotorTemp", () => ConvertTemp(_data.MotorTemp));
            this.AttachDelegate("Moza.BaseState", () => _data.BaseState);
            this.AttachDelegate("Moza.FfbStrength", () => _data.FfbStrength / 10);
            this.AttachDelegate("Moza.MaxAngle", () => _data.MaxAngle * 2);
        }

        private double ConvertTemp(int raw)
        {
            double celsius = raw / 100.0;
            return _data.UseFahrenheit ? celsius * 9.0 / 5.0 + 32.0 : celsius;
        }

        private void RegisterActions()
        {
            this.AddAction("Moza.ClearLeds", (a, b) =>
            {
                ClearLedsOnHardware();
                SimHub.Logging.Current.Info("[Moza] LEDs cleared via action");
            });
        }

        /// <summary>
        /// Send all-off to wheel and dash LEDs via device manager.
        /// </summary>
        private void ClearLedsOnHardware()
        {
            if (!_connection.IsConnected) return;
            _deviceManager.WriteArray("wheel-send-rpm-telemetry", new byte[] { 0, 0 });
            _deviceManager.WriteArray("wheel-send-buttons-telemetry", new byte[] { 0, 0 });
            _deviceManager.WriteSetting("wheel-old-send-telemetry", 0);
            _deviceManager.WriteSetting("dash-send-telemetry", 0);
        }

        // ===== Telemetry =====

        internal TelemetrySender TelemetrySender => _telemetrySender;

        /// <summary>Apply settings from MozaPluginSettings to the TelemetrySender.</summary>
        internal void ApplyTelemetrySettings()
        {
            if (_telemetrySender == null) return;
            var s = _settings;

            _telemetrySender.FlagByteMode = s.TelemetryFlagByteMode;

            // Resolve the active multi-stream profile
            MultiStreamProfile? profile = null;
            if (!string.IsNullOrEmpty(s.TelemetryMzdashPath) && System.IO.File.Exists(s.TelemetryMzdashPath))
                profile = DashProfileStore.ParseMzdash(s.TelemetryMzdashPath);

            if (profile == null)
            {
                var builtins = DashProfileStore.BuiltinProfiles;
                if (builtins.Count > 0)
                {
                    if (!string.IsNullOrEmpty(s.TelemetryProfileName))
                        profile = FindProfile(builtins, s.TelemetryProfileName);
                    profile ??= builtins[0];
                }
            }

            _telemetrySender.Profile = profile;
        }

        internal void SetTelemetryEnabled(bool enabled)
        {
            _settings.TelemetryEnabled = enabled;
            SaveSettings();
            if (enabled)
            {
                ApplyTelemetrySettings();
                StartTelemetryIfReady();
            }
            else
            {
                _telemetrySender?.Stop();
                // Reset guards so re-enable can start a fresh session.
                // Without this, FramesSent > 0 and _telemetryStartRequested == 1
                // cause StartTelemetryIfReady() to bail out on re-enable.
                Interlocked.Exchange(ref _telemetryStartRequested, 0);
            }
        }

        /// <summary>
        /// Start the telemetry sender only if preconditions are met:
        /// connection is up, a wheel is detected, telemetry is enabled, and
        /// a profile is loaded. Called from device detection and profile application.
        /// The session open probe requires the wheel to be present and responsive —
        /// starting before detection wastes time and may send to an uninitialized device.
        ///
        /// Dispatches Start() to a background thread because ProbeAndOpenSessions()
        /// blocks waiting for ack responses delivered by the serial read thread.
        /// Calling Start() directly on the read thread would deadlock.
        /// </summary>
        private void StartTelemetryIfReady()
        {
            if (_telemetrySender == null) return;
            if (!_settings.TelemetryEnabled) return;
            if (!_connection.IsConnected) return;
            if (!_newWheelDetected && !_oldWheelDetected) return;
            if (_telemetrySender.Profile == null) return;

            // Already running — don't restart (avoids re-probing ports mid-session)
            if (_telemetrySender.FramesSent > 0) return;

            // Prevent duplicate dispatch (multiple callers may pass the guards above
            // before the background thread increments FramesSent)
            if (Interlocked.CompareExchange(ref _telemetryStartRequested, 1, 0) != 0) return;

            SimHub.Logging.Current.Info("[Moza] Wheel detected and telemetry enabled — starting telemetry sender");
            ThreadPool.QueueUserWorkItem(_ => _telemetrySender.Start());
        }

        private static MultiStreamProfile? FindProfile(
            System.Collections.Generic.IReadOnlyList<MultiStreamProfile> profiles, string name)
        {
            foreach (var p in profiles)
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                    return p;
            return null;
        }

        private void TryConnect()
        {
            if (_connection.Connect())
            {
                _unmatched = 0;
                SimHub.Logging.Current.Info("[Moza] Connected to MOZA device");
                _deviceManager.ReadSettings(SettingsPollCommands);
                // Probe all wheel IDs immediately for fast detection
                _deviceManager.ProbeWheelDetection();
            }
        }

        private void PollStatus(object sender, ElapsedEventArgs e)
        {
            if (!_connection.IsConnected) return;

            _deviceManager.ReadSettings(StatusPollCommands);

            // Device detection probes — only sent until each device is found
            if (!_newWheelDetected && !_oldWheelDetected)
                _deviceManager.ProbeWheelDetection();
            if (!_dashDetected)
                _deviceManager.ReadSetting("dash-rpm-indicator-mode");
            if (!_handbrakeDetected)
                _deviceManager.ReadSetting("handbrake-direction");
            if (!_pedalsDetected)
                _deviceManager.ReadSetting("pedals-throttle-dir");
        }

        private volatile int _unmatched;

        private void OnMessageReceived(byte[] data)
        {
            // Filter firmware debug noise before parsing/logging
            if (data.Length >= 1 && data[0] == 0x0E)
                return;

            var result = MozaResponseParser.Parse(data);
            if (!result.HasValue)
            {
                _unmatched++;
                if (_unmatched <= 20 && data.Length >= 2)
                {
                    byte grp = MozaProtocol.ToggleBit7(data[0]);
                    byte dev = MozaProtocol.SwapNibbles(data[1]);
                    SimHub.Logging.Current.Info(
                        $"[Moza] Unmatched #{_unmatched}: rawGroup=0x{data[0]:X2} group=0x{grp:X2} " +
                        $"rawDev=0x{data[1]:X2} dev={dev} len={data.Length} " +
                        $"payload={BitConverter.ToString(data, 2, Math.Min(data.Length - 2, 8))}");
                }
                return;
            }

            var r = result.Value;
            _data.UpdateFromCommand(r.Name, r.IntValue);
            if (r.ArrayValue != null)
                _data.UpdateFromArray(r.Name, r.ArrayValue);

            DetectDevices(r.Name, r.IntValue, r.DeviceId);
        }

        /// <summary>
        /// Auto-detect connected devices based on response commands.
        ///   - dash-rpm-indicator-mode responds -> dashboard present
        ///   - wheel-telemetry-mode responds -> new protocol wheel (GS/FSR/CS/RS/TSW)
        ///   - wheel-rpm-value1 responds (but not telemetry-mode) -> old protocol wheel (ES)
        /// </summary>
        private void DetectDevices(string commandName, int value, byte deviceId)
        {
            if (value < 0) return; // No valid response

            // Update telemetry sender's heartbeat mask so it only pings detected devices
            if (deviceId >= 18 && deviceId <= 30)
                _telemetrySender.DetectedDeviceMask |= (1 << (deviceId - 18));

            // Base detection: IsBaseConnected was just set to true by UpdateFromCommand.
            // Re-apply the profile so base settings (FFB, damper, limit, etc.) are written to device.
            if (commandName == "base-mcu-temp" && !_baseDetected)
            {
                _baseDetected = true;
                SimHub.Logging.Current.Info("[Moza] Base detected, applying profile");
                var profile = _settings.ProfileStore.CurrentProfile;
                if (profile != null)
                    ApplyProfile(profile);
            }

            switch (commandName)
            {
                case "dash-rpm-indicator-mode":
                    if (!_dashDetected)
                    {
                        _dashDetected = true;
                        DeployDeviceDefinition("MOZA Dashboard", "MozaPlugin.Devices.Dash.device.json");
                        ApplySavedDashSettings();
                        SimHub.Logging.Current.Info("[Moza] Dashboard detected");
                    }
                    break;

                case "wheel-telemetry-mode":
                    if (!_newWheelDetected)
                    {
                        _newWheelDetected = true;
                        _deviceManager.LockWheelId(deviceId);
                        _deviceManager.ReadSetting("wheel-model-name");
                        _deviceManager.ReadSetting("wheel-sw-version");
                        _deviceManager.ReadSetting("wheel-hw-version");
                        _deviceManager.ReadSetting("wheel-serial-a");
                        _deviceManager.ReadSetting("wheel-serial-b");
                        ApplySavedWheelSettings();
                        SimHub.Logging.Current.Info($"[Moza] New-protocol wheel detected on ID {deviceId}");
                        StartTelemetryIfReady();
                    }
                    break;

                case "wheel-model-name":
                    // Only resolve per-model LED config for new-protocol wheels.
                    // ES wheels share device 0x13 with the base, so the model name
                    // response is the base name, not the wheel name.
                    if (_newWheelDetected)
                    {
                        WheelModelInfo = Devices.WheelModelInfo.FromModelName(_data.WheelModelName);
                        SimHub.Logging.Current.Info(
                            $"[Moza] Wheel model: {_data.WheelModelName} " +
                            $"(buttons={WheelModelInfo.ButtonLedCount}, flags={WheelModelInfo.HasFlagLeds})");
                        DeployDeviceDefinitionForModel(_data.WheelModelName);
                    }
                    else
                    {
                        SimHub.Logging.Current.Info(
                            $"[Moza] Wheel model (ES/base): {_data.WheelModelName}");
                    }
                    break;

                case "wheel-sw-version":
                    SimHub.Logging.Current.Info($"[Moza] Wheel FW: {_data.WheelSwVersion}");
                    break;

                case "wheel-serial-b":
                    if (!string.IsNullOrEmpty(_data.WheelSerialNumber))
                        SimHub.Logging.Current.Info($"[Moza] Wheel serial: {_data.WheelSerialNumber}");
                    break;

                case "wheel-rpm-value1":
                    if (!_newWheelDetected && !_oldWheelDetected)
                    {
                        _oldWheelDetected = true;
                        _deviceManager.LockWheelId(deviceId);
                        _deviceManager.ReadSetting("wheel-model-name");
                        _deviceManager.ReadSetting("wheel-sw-version");
                        _deviceManager.ReadSetting("wheel-hw-version");
                        _deviceManager.ReadSetting("wheel-serial-a");
                        _deviceManager.ReadSetting("wheel-serial-b");
                        ApplySavedWheelSettings();
                        DeployDeviceDefinitionForOldProto();
                        SimHub.Logging.Current.Info($"[Moza] Old-protocol wheel detected on ID {deviceId}");
                        StartTelemetryIfReady();
                    }
                    break;

                case "handbrake-direction":
                    if (!_handbrakeDetected)
                    {
                        _handbrakeDetected = true;
                        SimHub.Logging.Current.Info("[Moza] Handbrake detected");
                    }
                    break;

                case "pedals-throttle-dir":
                    if (!_pedalsDetected)
                    {
                        _pedalsDetected = true;
                        SimHub.Logging.Current.Info("[Moza] Pedals detected");
                    }
                    break;
            }
        }
        /// <summary>
        /// Send saved RPM timing settings to the wheel after detection.
        /// These values aren't retained by the wheel hardware.
        /// </summary>
        private void ApplySavedWheelSettings()
        {
            SimHub.Logging.Current.Info("[Moza] Applying saved wheel settings");

            // Pre-populate _data from saved settings so the UI shows correct values
            // immediately, before device responses arrive.
            if (_settings.WheelTelemetryMode >= 0)
                _data.WheelTelemetryMode = _settings.WheelTelemetryMode;
            if (_settings.WheelIdleEffect >= 0)
                _data.WheelTelemetryIdleEffect = _settings.WheelIdleEffect;
            if (_settings.WheelButtonsIdleEffect >= 0)
                _data.WheelButtonsIdleEffect = _settings.WheelButtonsIdleEffect;
            if (_settings.WheelRpmIndicatorMode >= 0)
                _data.WheelRpmIndicatorMode = _settings.WheelRpmIndicatorMode;
            if (_settings.WheelRpmDisplayMode >= 0)
                _data.WheelRpmDisplayMode = _settings.WheelRpmDisplayMode;
            _data.WheelRpmBrightness = _settings.WheelRpmBrightness;
            _data.WheelButtonsBrightness = _settings.WheelButtonsBrightness;
            _data.WheelFlagsBrightness = _settings.WheelFlagsBrightness;
            _data.WheelESRpmBrightness = _settings.WheelESRpmBrightness;

            // LED mode (only if previously saved)
            if (_settings.WheelTelemetryMode >= 0)
                _deviceManager.WriteSetting("wheel-telemetry-mode", _settings.WheelTelemetryMode);
            if (_settings.WheelIdleEffect >= 0)
                _deviceManager.WriteSetting("wheel-telemetry-idle-effect", _settings.WheelIdleEffect);
            if (_settings.WheelButtonsIdleEffect >= 0)
                _deviceManager.WriteSetting("wheel-buttons-idle-effect", _settings.WheelButtonsIdleEffect);

            // ES/Old wheel modes
            if (_settings.WheelRpmIndicatorMode >= 0)
                _deviceManager.WriteSetting("wheel-rpm-indicator-mode", _settings.WheelRpmIndicatorMode + 1); // display→raw
            if (_settings.WheelRpmDisplayMode >= 0)
                _deviceManager.WriteSetting("wheel-set-rpm-display-mode", _settings.WheelRpmDisplayMode);

            // Brightness
            _deviceManager.WriteSetting("wheel-rpm-brightness", _settings.WheelRpmBrightness);
            _deviceManager.WriteSetting("wheel-buttons-brightness", _settings.WheelButtonsBrightness);
            _deviceManager.WriteSetting("wheel-flags-brightness", _settings.WheelFlagsBrightness);
            _deviceManager.WriteSetting("wheel-old-rpm-brightness", _settings.WheelESRpmBrightness);
        }

        /// <summary>
        /// Send saved dash brightness settings after detection.
        /// </summary>
        private void ApplySavedDashSettings()
        {
            SimHub.Logging.Current.Info("[Moza] Applying saved dash settings");

            // Pre-populate _data from saved settings so the UI shows correct values
            _data.DashRpmBrightness = _settings.DashRpmBrightness;
            _data.DashFlagsBrightness = _settings.DashFlagsBrightness;

            // Brightness
            _deviceManager.WriteSetting("dash-rpm-brightness", _settings.DashRpmBrightness);
            _deviceManager.WriteSetting("dash-flags-brightness", _settings.DashFlagsBrightness);
        }



        // ===== Profile system (SimHub native) =====

        /// <summary>
        /// Initialize the native SimHub profile system.
        /// ProfileSettingsBase.Init() reads the current game from PluginManager and selects the right profile.
        /// </summary>
        private void InitProfileSystem()
        {
            var store = _settings.ProfileStore;

            // Ensure at least one default profile exists
            if (store.Profiles.Count == 0)
            {
                var defaultProfile = new MozaProfile { Name = "Default" };
                store.Profiles.Add(defaultProfile);
            }

            // Init reads PluginManager.Instance.GameName and selects the matching profile
            store.Init();

            // Subscribe to profile changes (game switch, manual selection)
            store.CurrentProfileChanged += OnProfileChanged;

            // Apply the initially selected profile
            if (store.CurrentProfile != null)
            {
                SimHub.Logging.Current.Info($"[Moza] Initial profile: {store.CurrentProfile.Name}");
                if (_settings.AutoApplyProfileOnLaunch)
                    ApplyProfile(store.CurrentProfile);
                else
                    SimHub.Logging.Current.Info("[Moza] Skipping auto-apply (disabled in Options)");
            }
        }

        private void OnProfileChanged(object sender, EventArgs e)
        {
            var profile = _settings.ProfileStore.CurrentProfile;
            if (profile != null)
            {
                SimHub.Logging.Current.Info($"[Moza] Profile changed: {profile.Name}");
                ApplyProfile(profile);
            }
        }

        /// <summary>
        /// Apply a profile: copy values into _settings and _data, write to device if connected.
        /// </summary>
        internal void ApplyProfile(MozaProfile profile)
        {
            SimHub.Logging.Current.Info($"[Moza] Applying profile: {profile.Name}");

            // Guard: a profile with all core base settings at zero was captured from
            // uninitialized device data (first-launch race condition). Reset them to
            // sentinels so they're skipped — the device keeps its own values.
            if (profile.Limit == 0 && profile.FfbStrength == 0 && profile.Torque == 0 && profile.Speed == 0)
            {
                SimHub.Logging.Current.Warn("[Moza] Profile has zeroed base settings — resetting to sentinels");
                profile.Limit = -1; profile.FfbStrength = -1; profile.Torque = -1; profile.Speed = -1;
                profile.Damper = -1; profile.Friction = -1; profile.Inertia = -1; profile.Spring = -1;
                profile.SpeedDamping = -1; profile.SpeedDampingPoint = -1;
                profile.NaturalInertia = -1; profile.SoftLimitStiffness = -1;
                profile.SoftLimitRetain = -1; profile.FfbReverse = -1; profile.Protection = -1;
                profile.GameDamper = -1; profile.GameFriction = -1;
                profile.GameInertia = -1; profile.GameSpring = -1;
                profile.WorkMode = -1;
            }

            // --- Base/Motor settings → _data + device ---
            ApplyBaseSettingIfSet(profile.Limit, v => { _data.Limit = v; _data.MaxAngle = v; }, "base-limit", "base-max-angle");
            ApplyBaseSettingIfSet(profile.FfbStrength, v => _data.FfbStrength = v, "base-ffb-strength");
            ApplyBaseSettingIfSet(profile.Torque, v => _data.Torque = v, "base-torque");
            ApplyBaseSettingIfSet(profile.Speed, v => _data.Speed = v, "base-speed");
            ApplyBaseSettingIfSet(profile.Damper, v => _data.Damper = v, "base-damper");
            ApplyBaseSettingIfSet(profile.Friction, v => _data.Friction = v, "base-friction");
            ApplyBaseSettingIfSet(profile.Inertia, v => _data.Inertia = v, "base-inertia");
            ApplyBaseSettingIfSet(profile.Spring, v => _data.Spring = v, "base-spring");
            ApplyBaseSettingIfSet(profile.SpeedDamping, v => _data.SpeedDamping = v, "base-speed-damping");
            ApplyBaseSettingIfSet(profile.SpeedDampingPoint, v => _data.SpeedDampingPoint = v, "base-speed-damping-point");
            ApplyBaseSettingIfSet(profile.NaturalInertia, v => _data.NaturalInertia = v, "base-natural-inertia");
            ApplyBaseSettingIfSet(profile.SoftLimitStiffness, v => _data.SoftLimitStiffness = v, "base-soft-limit-stiffness");
            ApplyBaseSettingIfSet(profile.SoftLimitRetain, v => _data.SoftLimitRetain = v, "base-soft-limit-retain");
            ApplyBaseSettingIfSet(profile.FfbReverse, v => _data.FfbReverse = v, "base-ffb-reverse");
            ApplyBaseSettingIfSet(profile.Protection, v => _data.Protection = v, "base-protection");

            // Game effect gains
            ApplyBaseSettingIfSet(profile.GameDamper, v => _data.GameDamper = v, "main-set-damper-gain");
            ApplyBaseSettingIfSet(profile.GameFriction, v => _data.GameFriction = v, "main-set-friction-gain");
            ApplyBaseSettingIfSet(profile.GameInertia, v => _data.GameInertia = v, "main-set-inertia-gain");
            ApplyBaseSettingIfSet(profile.GameSpring, v => _data.GameSpring = v, "main-set-spring-gain");

            // Work mode
            ApplyBaseSettingIfSet(profile.WorkMode, v => _data.WorkMode = v, "main-set-work-mode");

            // --- Wheel LED settings → _settings + _data ---
            // When the device extension is active, it owns wheel LED settings
            // via SetSettings()/GetSettings() — skip to avoid conflicts.
            if (!DeviceExtensionActive)
            {
                if (profile.WheelTelemetryMode >= 0)
                {
                    _settings.WheelTelemetryMode = profile.WheelTelemetryMode;
                    _data.WheelTelemetryMode = profile.WheelTelemetryMode;
                }
                if (profile.WheelIdleEffect >= 0)
                {
                    _settings.WheelIdleEffect = profile.WheelIdleEffect;
                    _data.WheelTelemetryIdleEffect = profile.WheelIdleEffect;
                }
                if (profile.WheelButtonsIdleEffect >= 0)
                {
                    _settings.WheelButtonsIdleEffect = profile.WheelButtonsIdleEffect;
                    _data.WheelButtonsIdleEffect = profile.WheelButtonsIdleEffect;
                }
                if (profile.WheelRpmBrightness >= 0)
                {
                    _settings.WheelRpmBrightness = profile.WheelRpmBrightness;
                    _data.WheelRpmBrightness = profile.WheelRpmBrightness;
                }
                if (profile.WheelButtonsBrightness >= 0)
                {
                    _settings.WheelButtonsBrightness = profile.WheelButtonsBrightness;
                    _data.WheelButtonsBrightness = profile.WheelButtonsBrightness;
                }
                if (profile.WheelFlagsBrightness >= 0)
                {
                    _settings.WheelFlagsBrightness = profile.WheelFlagsBrightness;
                    _data.WheelFlagsBrightness = profile.WheelFlagsBrightness;
                }
                if (profile.WheelRpmIndicatorMode >= 0)
                {
                    _settings.WheelRpmIndicatorMode = profile.WheelRpmIndicatorMode;
                    _data.WheelRpmIndicatorMode = profile.WheelRpmIndicatorMode;
                }
                if (profile.WheelRpmDisplayMode >= 0)
                {
                    _settings.WheelRpmDisplayMode = profile.WheelRpmDisplayMode;
                    _data.WheelRpmDisplayMode = profile.WheelRpmDisplayMode;
                }
                if (profile.WheelESRpmBrightness >= 0)
                {
                    _settings.WheelESRpmBrightness = profile.WheelESRpmBrightness;
                    _data.WheelESRpmBrightness = profile.WheelESRpmBrightness;
                }

            }

            // Dashboard brightness
            if (profile.DashRpmBrightness >= 0)
            {
                _settings.DashRpmBrightness = profile.DashRpmBrightness;
                _data.DashRpmBrightness = profile.DashRpmBrightness;
            }
            if (profile.DashFlagsBrightness >= 0)
            {
                _settings.DashFlagsBrightness = profile.DashFlagsBrightness;
                _data.DashFlagsBrightness = profile.DashFlagsBrightness;
            }

            // --- FFB Equalizer ---
            // Equalizer uses -1000 as sentinel (valid range is 0 to 400, where 100 = default/flat)
            void ApplyEq(int val, System.Action<int> set, string cmd) { if (val > -1000) { set(val); if (_data.IsBaseConnected) _deviceManager.WriteSetting(cmd, val); } }
            ApplyEq(profile.Equalizer1, v => _data.Equalizer1 = v, "base-equalizer1");
            ApplyEq(profile.Equalizer2, v => _data.Equalizer2 = v, "base-equalizer2");
            ApplyEq(profile.Equalizer3, v => _data.Equalizer3 = v, "base-equalizer3");
            ApplyEq(profile.Equalizer4, v => _data.Equalizer4 = v, "base-equalizer4");
            ApplyEq(profile.Equalizer5, v => _data.Equalizer5 = v, "base-equalizer5");
            ApplyEq(profile.Equalizer6, v => _data.Equalizer6 = v, "base-equalizer6");

            // --- FFB Curve (X breakpoints always fixed at 20/40/60/80) ---
            // Always write fixed X breakpoints when base is connected (device may not persist them)
            SimHub.Logging.Current.Info($"[Moza] ApplyProfile curve: IsBaseConnected={_data.IsBaseConnected} Y1={profile.FfbCurveY1} Y2={profile.FfbCurveY2} Y3={profile.FfbCurveY3} Y4={profile.FfbCurveY4} Y5={profile.FfbCurveY5}");
            if (_data.IsBaseConnected)
            {
                _deviceManager.WriteSetting("base-ffb-curve-x1", 20);
                _deviceManager.WriteSetting("base-ffb-curve-x2", 40);
                _deviceManager.WriteSetting("base-ffb-curve-x3", 60);
                _deviceManager.WriteSetting("base-ffb-curve-x4", 80);
            }
            // Apply Y values from profile, or write linear defaults if none saved yet
            if (profile.FfbCurveY1 >= 0) ApplyBaseSettingIfSet(profile.FfbCurveY1, v => _data.FfbCurveY1 = v, "base-ffb-curve-y1");
            else if (_data.IsBaseConnected) _deviceManager.WriteSetting("base-ffb-curve-y1", _data.FfbCurveY1);
            if (profile.FfbCurveY2 >= 0) ApplyBaseSettingIfSet(profile.FfbCurveY2, v => _data.FfbCurveY2 = v, "base-ffb-curve-y2");
            else if (_data.IsBaseConnected) _deviceManager.WriteSetting("base-ffb-curve-y2", _data.FfbCurveY2);
            if (profile.FfbCurveY3 >= 0) ApplyBaseSettingIfSet(profile.FfbCurveY3, v => _data.FfbCurveY3 = v, "base-ffb-curve-y3");
            else if (_data.IsBaseConnected) _deviceManager.WriteSetting("base-ffb-curve-y3", _data.FfbCurveY3);
            if (profile.FfbCurveY4 >= 0) ApplyBaseSettingIfSet(profile.FfbCurveY4, v => _data.FfbCurveY4 = v, "base-ffb-curve-y4");
            else if (_data.IsBaseConnected) _deviceManager.WriteSetting("base-ffb-curve-y4", _data.FfbCurveY4);
            if (profile.FfbCurveY5 >= 0) ApplyBaseSettingIfSet(profile.FfbCurveY5, v => _data.FfbCurveY5 = v, "base-ffb-curve-y5");
            else if (_data.IsBaseConnected) _deviceManager.WriteSetting("base-ffb-curve-y5", _data.FfbCurveY5);

            // --- Handbrake settings → _data + device ---
            ApplyHandbrakeSettingIfSet(profile.HandbrakeMode, v => _data.HandbrakeMode = v, "handbrake-mode");
            ApplyHandbrakeSettingIfSet(profile.HandbrakeButtonThreshold, v => _data.HandbrakeButtonThreshold = v, "handbrake-button-threshold");
            ApplyHandbrakeSettingIfSet(profile.HandbrakeDirection, v => _data.HandbrakeDirection = v, "handbrake-direction");
            ApplyHandbrakeSettingIfSet(profile.HandbrakeMin, v => _data.HandbrakeMin = v, "handbrake-min");
            ApplyHandbrakeSettingIfSet(profile.HandbrakeMax, v => _data.HandbrakeMax = v, "handbrake-max");
            if (profile.HandbrakeCurve != null)
            {
                for (int i = 0; i < Math.Min(5, profile.HandbrakeCurve.Length); i++)
                {
                    int idx = i; int val = profile.HandbrakeCurve[i];
                    _data.HandbrakeCurve[idx] = val;
                    if (_handbrakeDetected) _deviceManager.WriteFloat($"handbrake-y{idx + 1}", val);
                }
            }

            // --- Pedal settings → _data + device ---
            ApplyPedalSettingIfSet(profile.PedalsThrottleDir, v => _data.PedalsThrottleDir = v, "pedals-throttle-dir");
            ApplyPedalSettingIfSet(profile.PedalsBrakeDir, v => _data.PedalsBrakeDir = v, "pedals-brake-dir");
            if (profile.PedalsBrakeAngleRatio >= 0) { _data.PedalsBrakeAngleRatio = profile.PedalsBrakeAngleRatio; if (_pedalsDetected) _deviceManager.WriteFloat("pedals-brake-angle-ratio", profile.PedalsBrakeAngleRatio); }
            ApplyPedalSettingIfSet(profile.PedalsClutchDir, v => _data.PedalsClutchDir = v, "pedals-clutch-dir");
            ApplyCurveIfSet(profile.PedalsThrottleCurve, _data.PedalsThrottleCurve, "pedals-throttle-y", _pedalsDetected);
            ApplyCurveIfSet(profile.PedalsBrakeCurve, _data.PedalsBrakeCurve, "pedals-brake-y", _pedalsDetected);
            ApplyCurveIfSet(profile.PedalsClutchCurve, _data.PedalsClutchCurve, "pedals-clutch-y", _pedalsDetected);

            // --- Colors → _data ---
            if (!DeviceExtensionActive)
            {
                MozaProfile.UnpackColorsInto(profile.WheelRpmColors, _data.WheelRpmColors);
                MozaProfile.UnpackColorsInto(profile.WheelRpmBlinkColors, _data.WheelRpmBlinkColors);
                MozaProfile.UnpackColorsInto(profile.WheelButtonColors, _data.WheelButtonColors);
                MozaProfile.UnpackColorsInto(profile.WheelFlagColors, _data.WheelFlagColors);
                if (profile.WheelIdleColor != null && profile.WheelIdleColor.Length > 0)
                {
                    var rgb = MozaProfile.UnpackColor(profile.WheelIdleColor[0]);
                    _data.WheelIdleColor[0] = rgb[0];
                    _data.WheelIdleColor[1] = rgb[1];
                    _data.WheelIdleColor[2] = rgb[2];
                }
                MozaProfile.UnpackColorsInto(profile.WheelESRpmColors, _data.WheelESRpmColors);
                _settings.WheelRpmBlinkColors = profile.WheelRpmBlinkColors;
            }
            if (!DashDeviceExtensionActive)
            {
                MozaProfile.UnpackColorsInto(profile.DashRpmColors, _data.DashRpmColors);
                MozaProfile.UnpackColorsInto(profile.DashRpmBlinkColors, _data.DashRpmBlinkColors);
                MozaProfile.UnpackColorsInto(profile.DashFlagColors, _data.DashFlagColors);

                // Persist dash blink colors to settings (write-only, not polled from device)
                _settings.DashRpmBlinkColors = profile.DashRpmBlinkColors;
            }

            // --- Write to device if connected ---
            if (_data.IsBaseConnected)
            {
                WriteProfileWheelSettingsToDevice(profile);
                WriteProfileColorsToDevice(profile);
            }

            // Persist _settings without re-capturing _data into the profile.
            // The profile already has the values we just applied; capturing _data here
            // would corrupt the profile if concurrent device reads have overwritten _data
            // with stale values before the device has processed our writes.
            PersistSettings();
        }

        private void ApplyBaseSettingIfSet(int value, Action<int> setData, params string[] commands)
        {
            if (value < 0) return;
            setData(value);
            if (_data.IsBaseConnected)
            {
                foreach (var cmd in commands)
                    _deviceManager.WriteSetting(cmd, value);
            }
        }

        private void ApplyHandbrakeSettingIfSet(int value, Action<int> setData, string command)
        {
            if (value < 0) return;
            setData(value);
            if (_handbrakeDetected)
                _deviceManager.WriteSetting(command, value);
        }

        private void ApplyPedalSettingIfSet(int value, Action<int> setData, string command)
        {
            if (value < 0) return;
            setData(value);
            if (_pedalsDetected)
                _deviceManager.WriteSetting(command, value);
        }

        private void ApplyCurveIfSet(int[]? curve, int[] dataArray, string commandPrefix, bool deviceConnected)
        {
            if (curve == null) return;
            for (int i = 0; i < Math.Min(5, curve.Length); i++)
            {
                dataArray[i] = curve[i];
                if (deviceConnected)
                    _deviceManager.WriteFloat($"{commandPrefix}{i + 1}", curve[i]);
            }
        }

        private void WriteProfileWheelSettingsToDevice(MozaProfile profile)
        {
            // When device extension is active, it owns wheel LED settings
            if (!DeviceExtensionActive)
            {
                // New wheel settings
                if (_newWheelDetected)
                {
                    if (profile.WheelTelemetryMode >= 0)
                        _deviceManager.WriteSetting("wheel-telemetry-mode", profile.WheelTelemetryMode);
                    if (profile.WheelIdleEffect >= 0)
                        _deviceManager.WriteSetting("wheel-telemetry-idle-effect", profile.WheelIdleEffect);
                    if (profile.WheelButtonsIdleEffect >= 0)
                        _deviceManager.WriteSetting("wheel-buttons-idle-effect", profile.WheelButtonsIdleEffect);
                    if (profile.WheelRpmBrightness >= 0)
                        _deviceManager.WriteSetting("wheel-rpm-brightness", profile.WheelRpmBrightness);
                    if (profile.WheelButtonsBrightness >= 0)
                        _deviceManager.WriteSetting("wheel-buttons-brightness", profile.WheelButtonsBrightness);
                    if (profile.WheelFlagsBrightness >= 0)
                        _deviceManager.WriteSetting("wheel-flags-brightness", profile.WheelFlagsBrightness);
                }

                // ES/Old wheel settings
                if (_oldWheelDetected)
                {
                    if (profile.WheelRpmIndicatorMode >= 0)
                        _deviceManager.WriteSetting("wheel-rpm-indicator-mode", profile.WheelRpmIndicatorMode + 1); // display→raw
                    if (profile.WheelRpmDisplayMode >= 0)
                        _deviceManager.WriteSetting("wheel-set-rpm-display-mode", profile.WheelRpmDisplayMode);
                    if (profile.WheelESRpmBrightness >= 0)
                        _deviceManager.WriteSetting("wheel-old-rpm-brightness", profile.WheelESRpmBrightness);
                }
            }

            // Dashboard brightness (skip when dash device extension owns settings)
            if (!DashDeviceExtensionActive && _dashDetected)
            {
                if (profile.DashRpmBrightness >= 0)
                    _deviceManager.WriteSetting("dash-rpm-brightness", profile.DashRpmBrightness);
                if (profile.DashFlagsBrightness >= 0)
                    _deviceManager.WriteSetting("dash-flags-brightness", profile.DashFlagsBrightness);
            }
        }

        private void WriteProfileColorsToDevice(MozaProfile profile)
        {
            // Wheel colors (skip when device extension owns wheel settings)
            if (!DeviceExtensionActive)
            {
                WriteColorArray(profile.WheelRpmColors, "wheel-rpm-color", 10);
                WriteColorArray(profile.WheelRpmBlinkColors, "wheel-rpm-blink-color", 10);
                WriteColorArray(profile.WheelButtonColors, "wheel-button-color", 14);
                WriteColorArray(profile.WheelFlagColors, "wheel-flag-color", 6);
                if (profile.WheelIdleColor != null && profile.WheelIdleColor.Length > 0)
                {
                    var rgb = MozaProfile.UnpackColor(profile.WheelIdleColor[0]);
                    _deviceManager.WriteColor("wheel-idle-color", rgb[0], rgb[1], rgb[2]);
                }
                WriteColorArray(profile.WheelESRpmColors, "wheel-old-rpm-color", 10);
            }

            // Dash colors (skip when dash device extension owns settings)
            if (!DashDeviceExtensionActive)
            {
                WriteColorArray(profile.DashRpmColors, "dash-rpm-color", 10);
                WriteColorArray(profile.DashRpmBlinkColors, "dash-rpm-blink-color", 10);
                WriteColorArray(profile.DashFlagColors, "dash-flag-color", 6);
            }
        }

        private void WriteColorArray(int[]? packedColors, string commandPrefix, int count)
        {
            if (packedColors == null) return;
            int len = Math.Min(packedColors.Length, count);
            for (int i = 0; i < len; i++)
            {
                var rgb = MozaProfile.UnpackColor(packedColors[i]);
                _deviceManager.WriteColor($"{commandPrefix}{i + 1}", rgb[0], rgb[1], rgb[2]);
            }
        }

        /// <summary>
        /// Apply wheel settings from the SimHub device extension profile system.
        /// Updates _settings, _data, and writes to hardware if connected.
        /// </summary>
        internal void ApplyWheelExtensionSettings(MozaWheelExtensionSettings extSettings)
        {
            SimHub.Logging.Current.Info("[Moza] Applying wheel device extension settings");

            // Update _settings and _data in-memory
            extSettings.ApplyTo(_settings, _data);

            // Persist blink colors
            _settings.WheelRpmBlinkColors = extSettings.WheelRpmBlinkColors;

            // Write to hardware if connected
            if (_data.IsBaseConnected)
            {
                // Wheel mode/brightness settings
                if (_newWheelDetected)
                {
                    if (extSettings.WheelTelemetryMode >= 0)
                        _deviceManager.WriteSetting("wheel-telemetry-mode", extSettings.WheelTelemetryMode);
                    if (extSettings.WheelIdleEffect >= 0)
                        _deviceManager.WriteSetting("wheel-telemetry-idle-effect", extSettings.WheelIdleEffect);
                    if (extSettings.WheelButtonsIdleEffect >= 0)
                        _deviceManager.WriteSetting("wheel-buttons-idle-effect", extSettings.WheelButtonsIdleEffect);
                    if (extSettings.WheelRpmBrightness >= 0)
                        _deviceManager.WriteSetting("wheel-rpm-brightness", extSettings.WheelRpmBrightness);
                    if (extSettings.WheelButtonsBrightness >= 0)
                        _deviceManager.WriteSetting("wheel-buttons-brightness", extSettings.WheelButtonsBrightness);
                    if (extSettings.WheelFlagsBrightness >= 0)
                        _deviceManager.WriteSetting("wheel-flags-brightness", extSettings.WheelFlagsBrightness);
                }

                if (_oldWheelDetected)
                {
                    if (extSettings.WheelRpmIndicatorMode >= 0)
                        _deviceManager.WriteSetting("wheel-rpm-indicator-mode", extSettings.WheelRpmIndicatorMode + 1);
                    if (extSettings.WheelRpmDisplayMode >= 0)
                        _deviceManager.WriteSetting("wheel-set-rpm-display-mode", extSettings.WheelRpmDisplayMode);
                    if (extSettings.WheelESRpmBrightness >= 0)
                        _deviceManager.WriteSetting("wheel-old-rpm-brightness", extSettings.WheelESRpmBrightness);
                }

                // Colors
                WriteColorArray(extSettings.WheelRpmColors, "wheel-rpm-color", 10);
                WriteColorArray(extSettings.WheelRpmBlinkColors, "wheel-rpm-blink-color", 10);
                WriteColorArray(extSettings.WheelButtonColors, "wheel-button-color", 14);
                WriteColorArray(extSettings.WheelFlagColors, "wheel-flag-color", 6);
                if (extSettings.WheelIdleColor != null && extSettings.WheelIdleColor.Length > 0)
                {
                    var rgb = MozaProfile.UnpackColor(extSettings.WheelIdleColor[0]);
                    _deviceManager.WriteColor("wheel-idle-color", rgb[0], rgb[1], rgb[2]);
                }
                WriteColorArray(extSettings.WheelESRpmColors, "wheel-old-rpm-color", 10);
            }

            PersistSettings();

            // Apply telemetry settings if present in this profile
            if (extSettings.TelemetrySettingsPresent)
            {
                if (_settings.TelemetryEnabled)
                {
                    ApplyTelemetrySettings();
                    StartTelemetryIfReady();
                }
                else
                {
                    _telemetrySender?.Stop();
                }
            }
        }

        /// <summary>
        /// Apply dash settings from the SimHub device extension profile system.
        /// Updates _settings, _data, and writes to hardware if connected.
        /// </summary>
        internal void ApplyDashExtensionSettings(MozaDashExtensionSettings extSettings)
        {
            SimHub.Logging.Current.Info("[Moza] Applying dash device extension settings");

            // Update _settings and _data in-memory
            extSettings.ApplyTo(_settings, _data);

            // Persist blink colors
            _settings.DashRpmBlinkColors = extSettings.DashRpmBlinkColors;

            // Write to hardware if connected
            if (_data.IsBaseConnected && _dashDetected)
            {
                if (extSettings.DashRpmBrightness >= 0)
                    _deviceManager.WriteSetting("dash-rpm-brightness", extSettings.DashRpmBrightness);
                if (extSettings.DashFlagsBrightness >= 0)
                    _deviceManager.WriteSetting("dash-flags-brightness", extSettings.DashFlagsBrightness);
                if (extSettings.DashRpmIndicatorMode >= 0)
                    _deviceManager.WriteSetting("dash-rpm-indicator-mode", extSettings.DashRpmIndicatorMode);
                if (extSettings.DashFlagsIndicatorMode >= 0)
                    _deviceManager.WriteSetting("dash-flags-indicator-mode", extSettings.DashFlagsIndicatorMode);
                if (extSettings.DashRpmDisplayMode >= 0)
                    _deviceManager.WriteSetting("dash-rpm-display-mode", extSettings.DashRpmDisplayMode);

                // Colors
                WriteColorArray(extSettings.DashRpmColors, "dash-rpm-color", 10);
                WriteColorArray(extSettings.DashRpmBlinkColors, "dash-rpm-blink-color", 10);
                WriteColorArray(extSettings.DashFlagColors, "dash-flag-color", 6);
            }

            PersistSettings();
        }

        /// <summary>
        /// Deploy a dynamically generated device definition for a new-protocol wheel.
        /// Uses WheelModelInfo for button count (defaults for unknown models) and
        /// deterministic GUIDs for device identity.
        /// Called once when the wheel model name is first received from firmware.
        /// </summary>
        private void DeployDeviceDefinitionForModel(string modelName)
        {
            var prefix = WheelModelInfo.ExtractPrefix(modelName);
            var friendlyName = WheelModelInfo.GetFriendlyName(prefix);
            var guid = MozaDeviceConstants.ResolveWheelGuid(prefix);
            var modelInfo = WheelModelInfo.FromModelName(modelName);
            var deviceName = "MOZA " + friendlyName;

            DeployGeneratedWheelDefinition(deviceName, guid, friendlyName, modelInfo.ButtonLedCount);
        }

        private void DeployGeneratedWheelDefinition(string deviceName, string guid, string productName, int buttonCount)
        {
            try
            {
                var simHubDir = AppDomain.CurrentDomain.BaseDirectory;
                var userDefsDir = Path.Combine(simHubDir, "DevicesDefinitions", "User");
                var deviceDir = Path.Combine(userDefsDir, deviceName);
                var deviceJsonPath = Path.Combine(deviceDir, "device.json");

                if (File.Exists(deviceJsonPath))
                    return;

                Directory.CreateDirectory(deviceDir);

                var pid = _connection.DiscoveredPid ?? "0x0004";
                var json = GenerateWheelDeviceJson(guid, productName, buttonCount, pid);
                File.WriteAllText(deviceJsonPath, json);

                DeviceDefinitionDeployed = true;
                SimHub.Logging.Current.Info(
                    $"[Moza] Deployed device definition: {deviceName} " +
                    $"(guid={guid}, buttons={buttonCount}, pid={pid}, restart SimHub to add it)");
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"[Moza] Error deploying device definition '{deviceName}': {ex.Message}");
            }
        }

        private static string GenerateWheelDeviceJson(string guid, string productName, int buttonCount, string pid)
        {
            var physItems = new JArray();

            // RPM LEDs: 10 slots (first has the mapping, rest are empty continuations)
            physItems.Add(new JObject
            {
                ["SourceRole"] = 1,
                ["SourceIndex"] = 0,
                ["RepeatCount"] = MozaDeviceConstants.RpmLedCount,
                ["RepeatMode"] = 1
            });
            for (int i = 1; i < MozaDeviceConstants.RpmLedCount; i++)
                physItems.Add(new JObject());

            // Button LEDs: buttonCount slots
            physItems.Add(new JObject
            {
                ["SourceRole"] = 2,
                ["SourceIndex"] = 0,
                ["RepeatCount"] = buttonCount,
                ["RepeatMode"] = 1
            });
            for (int i = 1; i < buttonCount; i++)
                physItems.Add(new JObject());

            var buttonItems = new JArray();
            for (int i = 0; i < buttonCount; i++)
            {
                buttonItems.Add(new JObject
                {
                    ["Left"] = 20,
                    ["Top"] = 20,
                    ["Width"] = 40
                });
            }

            var device = new JObject
            {
                ["DescriptorUniqueId"] = guid,
                ["SchemaVersion"] = 1,
                ["MinimumSimHubVersion"] = "9.11.8",
                ["DeviceDescription"] = new JObject
                {
                    ["BrandName"] = "MOZA",
                    ["ProductName"] = productName
                },
                ["LedsFeature"] = new JObject
                {
                    ["IsIndividualLedsSectionEnabled"] = true,
                    ["PhysicalLedsMappings"] = new JObject { ["Items"] = physItems },
                    ["LogicalTelemetryLeds"] = new JObject
                    {
                        ["LedCount"] = MozaDeviceConstants.RpmLedCount,
                        ["Segments"] = new JArray(),
                        ["IsEnabled"] = true
                    },
                    ["LogicalButtonsSection"] = new JObject
                    {
                        ["IsButtonEditorEnabled"] = false,
                        ["Items"] = buttonItems,
                        ["IsEnabled"] = true
                    },
                    ["IsEnabled"] = true
                },
                ["HardwareInterface"] = new JObject
                {
                    ["HardwareInterface"] = new JObject
                    {
                        ["TypeName"] = "LedsStandardHIDProtocol",
                        ["HIDUsagePage"] = "0xFF00",
                        ["HIDUsage"] = "0x77",
                        ["HIDReportId"] = "0x68",
                        ["HIDReportSize"] = 64,
                        ["DeviceDetection"] = new JObject
                        {
                            ["Vid"] = "0x346E",
                            ["Pid"] = pid
                        }
                    }
                },
                ["IsLocked"] = true
            };

            return device.ToString(Newtonsoft.Json.Formatting.Indented);
        }

        /// <summary>
        /// Deploy the old-protocol wheel device definition.
        /// Called once when an ES wheel is detected.
        /// </summary>
        private void DeployDeviceDefinitionForOldProto()
        {
            DeployDeviceDefinition("MOZA Old Protocol Wheel", "MozaPlugin.Devices.WheelOldProto.device.json");
        }

        private void DeployDeviceDefinition(string deviceName, string resourceName)
        {
            try
            {
                var simHubDir = AppDomain.CurrentDomain.BaseDirectory;
                var userDefsDir = Path.Combine(simHubDir, "DevicesDefinitions", "User");
                var deviceDir = Path.Combine(userDefsDir, deviceName);
                var deviceJsonPath = Path.Combine(deviceDir, "device.json");

                if (File.Exists(deviceJsonPath))
                    return;

                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        SimHub.Logging.Current.Warn($"[Moza] Embedded resource not found: {resourceName}");
                        return;
                    }

                    Directory.CreateDirectory(deviceDir);

                    // Read the template JSON and patch the PID if we discovered one
                    string json;
                    using (var reader = new StreamReader(stream))
                    {
                        json = reader.ReadToEnd();
                    }

                    var discoveredPid = _connection.DiscoveredPid;
                    if (discoveredPid != null)
                    {
                        json = json.Replace("__DETECT_PID__", discoveredPid);
                        SimHub.Logging.Current.Info($"[Moza] Patched device PID to {discoveredPid} for {deviceName}");
                    }
                    else
                    {
                        // Fallback: no PID discovered (e.g. probe-based discovery under Wine).
                        // Use 0x0004 as a reasonable default.
                        json = json.Replace("__DETECT_PID__", "0x0004");
                        SimHub.Logging.Current.Info($"[Moza] No PID discovered, using fallback 0x0004 for {deviceName}");
                    }

                    File.WriteAllText(deviceJsonPath, json);
                }

                DeviceDefinitionDeployed = true;
                SimHub.Logging.Current.Info($"[Moza] Deployed device definition: {deviceName} (restart SimHub to add it)");
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"[Moza] Error deploying device definition '{deviceName}': {ex.Message}");
            }
        }

    }
}
