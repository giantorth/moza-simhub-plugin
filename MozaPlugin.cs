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
        private int _connectingFlag;
        private MozaHidReader _hidReader = null!;
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
        private bool _hubDetected;

        // Guard against concurrent/duplicate telemetry Start() dispatch
        private int _telemetryStartRequested;

        // Set during End() so in-flight callbacks can bail out.
        internal static volatile bool IsShuttingDown;

        // Debounce disk writes during rapid slider changes
        private Timer? _saveDebounceTimer;

        // Tracks the ProfileStore we subscribed CurrentProfileChanged on, so we can
        // detach when ClearSettings replaces _settings (orphaned subscription would
        // otherwise mutate plugin state via captured `this` from a dead store).
        private MozaProfileStore? _subscribedProfileStore;

        private static readonly string[] StatusPollCommands = new[]
        {
            "base-mcu-temp", "base-mosfet-temp", "base-motor-temp",
            "base-state",
        };

        // --- Per-device settings read commands ---
        // These are sent only after the corresponding device is detected,
        // rather than blasting all commands on connect.

        private static readonly string[] BaseSettingsReadCommands = new[]
        {
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
        };

        private static readonly string[] NewWheelSettingsReadCommands = new[]
        {
            "wheel-telemetry-mode", "wheel-telemetry-idle-effect",
            "wheel-buttons-idle-effect",
            "wheel-rpm-brightness", "wheel-buttons-brightness", "wheel-flags-brightness",
            "wheel-idle-mode", "wheel-idle-timeout", "wheel-idle-speed",
            "wheel-idle-color",
            "wheel-paddles-mode", "wheel-clutch-point", "wheel-knob-mode", "wheel-stick-mode",
            // Per-encoder signal mode probe — silent on firmware without [42, N] support
            "wheel-knob-signal-mode0", "wheel-knob-signal-mode1", "wheel-knob-signal-mode2",
            "wheel-knob-signal-mode3", "wheel-knob-signal-mode4",
            // RPM colors (up to 18 — KS Pro max)
            "wheel-rpm-color1", "wheel-rpm-color2", "wheel-rpm-color3",
            "wheel-rpm-color4", "wheel-rpm-color5", "wheel-rpm-color6",
            "wheel-rpm-color7", "wheel-rpm-color8", "wheel-rpm-color9",
            "wheel-rpm-color10", "wheel-rpm-color11", "wheel-rpm-color12",
            "wheel-rpm-color13", "wheel-rpm-color14", "wheel-rpm-color15",
            "wheel-rpm-color16", "wheel-rpm-color17", "wheel-rpm-color18",
            // Button colors
            "wheel-button-color1",  "wheel-button-color2",  "wheel-button-color3",
            "wheel-button-color4",  "wheel-button-color5",  "wheel-button-color6",
            "wheel-button-color7",  "wheel-button-color8",  "wheel-button-color9",
            "wheel-button-color10", "wheel-button-color11", "wheel-button-color12",
            "wheel-button-color13", "wheel-button-color14",
            // Flag colors
            "wheel-flag-color1", "wheel-flag-color2", "wheel-flag-color3",
            "wheel-flag-color4", "wheel-flag-color5", "wheel-flag-color6",
            // Extended LED group presence probes (Single/Rotary/Ambient).
            // A brightness response flips IsWheelLedGroupPresent for that group.
            "wheel-group2-brightness", "wheel-group3-brightness", "wheel-group4-brightness",
        };

        private static readonly string[] OldWheelSettingsReadCommands = new[]
        {
            "wheel-rpm-indicator-mode", "wheel-get-rpm-display-mode",
            "wheel-old-rpm-brightness",
            "wheel-old-rpm-color1", "wheel-old-rpm-color2", "wheel-old-rpm-color3",
            "wheel-old-rpm-color4", "wheel-old-rpm-color5", "wheel-old-rpm-color6",
            "wheel-old-rpm-color7", "wheel-old-rpm-color8", "wheel-old-rpm-color9",
            "wheel-old-rpm-color10",
        };

        private static readonly string[] DashSettingsReadCommands = new[]
        {
            "dash-rpm-indicator-mode", "dash-flags-indicator-mode",
            "dash-rpm-display-mode",
            "dash-rpm-brightness", "dash-flags-brightness",
            "dash-rpm-color1", "dash-rpm-color2", "dash-rpm-color3",
            "dash-rpm-color4", "dash-rpm-color5", "dash-rpm-color6",
            "dash-rpm-color7", "dash-rpm-color8", "dash-rpm-color9",
            "dash-rpm-color10",
            "dash-flag-color1", "dash-flag-color2", "dash-flag-color3",
            "dash-flag-color4", "dash-flag-color5", "dash-flag-color6",
        };

        private static readonly string[] HandbrakeSettingsReadCommands = new[]
        {
            "handbrake-direction", "handbrake-min", "handbrake-max",
            "handbrake-mode", "handbrake-button-threshold",
            "handbrake-y1", "handbrake-y2", "handbrake-y3", "handbrake-y4", "handbrake-y5",
        };

        private static readonly string[] PedalsSettingsReadCommands = new[]
        {
            "pedals-throttle-dir", "pedals-throttle-min", "pedals-throttle-max",
            "pedals-brake-dir", "pedals-brake-min", "pedals-brake-max", "pedals-brake-angle-ratio",
            "pedals-clutch-dir", "pedals-clutch-min", "pedals-clutch-max",
            "pedals-throttle-y1", "pedals-throttle-y2", "pedals-throttle-y3", "pedals-throttle-y4", "pedals-throttle-y5",
            "pedals-brake-y1", "pedals-brake-y2", "pedals-brake-y3", "pedals-brake-y4", "pedals-brake-y5",
            "pedals-clutch-y1", "pedals-clutch-y2", "pedals-clutch-y3", "pedals-clutch-y4", "pedals-clutch-y5",
        };

        private static readonly string[] HubReadCommands = new[]
        {
            "hub-base-power", "hub-port1-power", "hub-port2-power", "hub-port3-power",
            "hub-pedals1-power", "hub-pedals2-power", "hub-pedals3-power",
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
        /// Extended LED groups detected on the connected wheel (indices 2..4 for Single,
        /// Rotary, Ambient per rs21_parameter.db). A group is flagged true when the wheel
        /// answers the group's brightness read during the post-connect probe.
        /// Groups 0/1 (RPM/Buttons) are not tracked here — their presence is implied by
        /// any new-protocol wheel.
        /// </summary>
        // Bit `g` set => group g detected. Stored as int so all reads/writes go
        // through Interlocked, giving lock-free atomic visibility between the
        // serial-message thread (which sets bits as group probes respond) and
        // any reader (UI, device extensions, poll timer).
        private int _wheelLedGroupMask;
        internal bool IsWheelLedGroupPresent(int group)
        {
            if (group < 2 || group > 4) return false;
            return (Volatile.Read(ref _wheelLedGroupMask) & (1 << group)) != 0;
        }
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
        internal bool IsHubDetected => _hubDetected;

        /// <summary>True if the wheel's internal Display sub-device responded to probe.</summary>
        internal bool IsDisplayDetected => _telemetrySender?.DisplayDetected ?? false;

        /// <summary>Display sub-device model name (e.g. "Display"), or empty.</summary>
        internal string DisplayModelName => _telemetrySender?.DisplayModelName ?? "";
        internal MozaProfileStore ProfileStore => _settings?.ProfileStore!;

        public void Init(PluginManager pluginManager)
        {
            // Clear shutdown flag from any previous plugin instance in this process.
            // SimHub may load+unload plugins without restarting, leaving this true.
            IsShuttingDown = false;
            _pluginManager = pluginManager;

            try
            {
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

                _hidReader = new MozaHidReader(_data);
                _hidReader.Start();

                _telemetrySender = new TelemetrySender(_connection);
                ApplyTelemetrySettings();
                // Don't start telemetry here — defer until wheel is detected.
                // The session open probe requires the wheel to be present and responsive.
                // StartTelemetryIfReady() is called from DetectDevices() when the wheel
                // is first detected, and from profile application callbacks.

                // Publish Instance only after all resources are wired so a partial-init
                // throw can't leave a half-built plugin reachable from background callbacks.
                Instance = this;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"[Moza] Init failed: {ex}");
                CleanupPartialInit();
                throw;
            }
        }

        /// <summary>
        /// Tear down any resources allocated by Init() before it threw. Mirrors End()
        /// but tolerates null fields and never sets IsShuttingDown (caller may retry).
        /// </summary>
        private void CleanupPartialInit()
        {
            try { _pollTimer?.Stop(); } catch { }
            try { _reconnectTimer?.Stop(); } catch { }
            try { _saveDebounceTimer?.Stop(); } catch { }
            try { _telemetrySender?.Stop(); } catch { }
            try
            {
                if (_connection != null)
                    _connection.MessageReceived -= OnMessageReceived;
            }
            catch { }
            try
            {
                if (_subscribedProfileStore != null)
                {
                    _subscribedProfileStore.CurrentProfileChanged -= OnProfileChanged;
                    _subscribedProfileStore = null;
                }
            }
            catch { }
            try { _hidReader?.Dispose(); } catch { }
            try { _telemetrySender?.Dispose(); } catch { }
            try { _connection?.Dispose(); } catch { }
            try { _pollTimer?.Dispose(); } catch { }
            try { _reconnectTimer?.Dispose(); } catch { }
            try { _saveDebounceTimer?.Dispose(); } catch { }
            _saveDebounceTimer = null;
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            if (IsShuttingDown) return;
            _telemetrySender?.UpdateGameData(data.NewData);
        }

        public void End(PluginManager pluginManager)
        {
            IsShuttingDown = true;
            SimHub.Logging.Current.Info("[Moza] Shutting down plugin");

            // 1. Stop timers first so no new callbacks fire against disposed state.
            _saveDebounceTimer?.Stop();
            _pollTimer?.Stop();
            _reconnectTimer?.Stop();

            // 2. Persist settings and clear LEDs while connection is still alive.
            try { this.SaveCommonSettings("MozaPluginSettings", _settings); } catch { }
            try { ClearLedsOnHardware(); } catch { }

            // 3. Detach event subscriptions so any in-flight callback from a still-running
            //    background thread (HID/serial reader) cannot reach the plugin during teardown.
            try
            {
                if (_connection != null)
                    _connection.MessageReceived -= OnMessageReceived;
            }
            catch { }
            try
            {
                if (_subscribedProfileStore != null)
                    _subscribedProfileStore.CurrentProfileChanged -= OnProfileChanged;
                _subscribedProfileStore = null;
            }
            catch { }

            // 4. Stop telemetry send loop before tearing down connection.
            _telemetrySender?.Stop();

            // 5. Dispose I/O sources before dropping Instance so late callbacks
            //    see a live (but shutting-down) instance, not null.
            _hidReader?.Dispose();
            _telemetrySender?.Dispose();
            _connection?.Dispose();

            // 6. Dispose timers after I/O is gone.
            _saveDebounceTimer?.Dispose();
            _saveDebounceTimer = null;
            _pollTimer?.Dispose();
            _reconnectTimer?.Dispose();

            // 7. Null Instance last so any straggler callback can still no-op via IsShuttingDown.
            Instance = null;
        }

        internal MozaHidReader HidReader => _hidReader;

        internal void SaveSettings()
        {
            _settings.ProfileStore?.CurrentProfile?.CaptureFromCurrent(_settings, _data);
            // Mirror the active flat Wheel* fields into the per-wheel-model slot
            // so each physical wheel keeps its own brightness/mode/input settings
            // across reloads (see MozaPluginSettings.PerWheelSlots).
            _settings.MirrorActiveToSlot(_data?.WheelModelName);
            ScheduleSave();
        }

        private void PersistSettings()
        {
            ScheduleSave();
        }

        private readonly object _saveDebounceLock = new object();

        /// <summary>
        /// Debounce disk writes: restart a 500ms timer on each call.
        /// Prevents dozens of writes per second during rapid slider drags.
        /// </summary>
        private void ScheduleSave()
        {
            // Lazy-create under a lock — concurrent callers (UI thread + profile-change
            // thread) would otherwise both see null, each create a Timer, and the loser's
            // instance would leak (unstopped, unwatched, still referencing _settings).
            lock (_saveDebounceLock)
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
                SimHub.Logging.Current.Info("[Moza] Connection enabled");
            }
            else
            {
                _reconnectTimer.Stop();
                ClearLedsOnHardware();
                _telemetrySender.Stop();
                _connection?.Disconnect();
                _data.IsBaseConnected = false;
                _data.IsHubConnected = false;
                _data.ClearWheelIdentity();
                _baseDetected = false;
                _data.BaseSettingsRead = false;
                _dashDetected = false;
                _newWheelDetected = false;
                _oldWheelDetected = false;
                WheelModelInfo = null;
                _handbrakeDetected = false;
                _pedalsDetected = false;
                _hubDetected = false;
                if (_telemetrySender != null) _telemetrySender.HubPresent = false;
                _deviceManager.ResetWheelDetection();
                _telemetrySender.DetectedDeviceMask = 0;
                Interlocked.Exchange(ref _telemetryStartRequested, 0);
                _wheelPollMisses = 0;
                _lastKnownWheelModel = "";
                SimHub.Logging.Current.Info("[Moza] Connection disabled");
            }
        }

        public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new SettingsControl(this);
        }

        private void RegisterProperties(PluginManager pluginManager)
        {
            // Null-guard each delegate: SimHub may invoke property getters during
            // plugin reload windows where _data is unset, or after End() left fields
            // intact but mid-teardown. A throw inside a property getter destabilises
            // SimHub's property polling, so each getter returns a sentinel default.
            this.AttachDelegate("Moza.BaseConnected", () => _data?.IsBaseConnected ?? false);
            this.AttachDelegate("Moza.McuTemp", () => _data == null ? 0.0 : ConvertTemp(_data.McuTemp));
            this.AttachDelegate("Moza.MosfetTemp", () => _data == null ? 0.0 : ConvertTemp(_data.MosfetTemp));
            this.AttachDelegate("Moza.MotorTemp", () => _data == null ? 0.0 : ConvertTemp(_data.MotorTemp));
            this.AttachDelegate("Moza.BaseState", () => _data?.BaseState ?? 0);
            this.AttachDelegate("Moza.FfbStrength", () => (_data?.FfbStrength ?? 0) / 10);
            this.AttachDelegate("Moza.MaxAngle", () => (_data?.MaxAngle ?? 0) * 2);
        }

        private double ConvertTemp(int raw)
        {
            double celsius = raw / 100.0;
            return (_data?.UseFahrenheit ?? false) ? celsius * 9.0 / 5.0 + 32.0 : celsius;
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
            int rpmCount = WheelModelInfo?.RpmLedCount ?? 0;
            _deviceManager.WriteArray("wheel-send-rpm-telemetry",
                Devices.MozaLedDeviceManager.BuildRpmBitmaskBytes(0, rpmCount));
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

            // Legacy v3 (dropped — crashed some wheels) migrates to v2.
            if (s.TelemetryProtocolVersion != 0 && s.TelemetryProtocolVersion != 2)
                s.TelemetryProtocolVersion = 2;
            _telemetrySender.ProtocolVersion = s.TelemetryProtocolVersion;
            _telemetrySender.UploadDashboard = s.TelemetryUploadDashboard;

            // Resolve the active multi-stream profile and raw mzdash content
            MultiStreamProfile? profile = null;
            byte[]? mzdashContent = null;
            string mzdashName = "";

            if (!string.IsNullOrEmpty(s.TelemetryMzdashPath) && System.IO.File.Exists(s.TelemetryMzdashPath))
            {
                profile = DashProfileStore.ParseMzdash(s.TelemetryMzdashPath);
                mzdashContent = System.IO.File.ReadAllBytes(s.TelemetryMzdashPath);
                mzdashName = System.IO.Path.GetFileNameWithoutExtension(s.TelemetryMzdashPath);
            }

            if (profile == null)
            {
                var builtins = DashProfileStore.BuiltinProfiles;
                if (builtins.Count > 0)
                {
                    if (!string.IsNullOrEmpty(s.TelemetryProfileName))
                        profile = FindProfile(builtins, s.TelemetryProfileName);
                    profile ??= builtins[0];
                }

                // Load raw mzdash content from embedded resource for upload
                if (profile != null && mzdashContent == null)
                {
                    mzdashName = profile.Name;
                    string resourceName = $"MozaPlugin.Data.Dashes.{profile.Name.Replace(" ", "_")}.mzdash";
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using var stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream != null)
                    {
                        using var ms = new System.IO.MemoryStream();
                        stream.CopyTo(ms);
                        mzdashContent = ms.ToArray();
                    }
                }
            }

            // Apply user channel mappings for the selected dashboard (overrides
            // each channel's SimHubProperty string by URL). Must run before
            // assigning Profile so the frame builder binds resolvers correctly.
            if (profile != null)
            {
                string dashboardKey = DashboardProfileStore.GetDashboardKey(
                    s.TelemetryMzdashPath, profile);
                if (s.TelemetryChannelMappings != null &&
                    s.TelemetryChannelMappings.TryGetValue(dashboardKey, out var overrides))
                {
                    DashboardProfileStore.ApplyUserMappings(profile, overrides);
                }
            }

            _telemetrySender.PropertyResolver = ResolvePropertyAsDouble;
            _telemetrySender.Profile = profile;
            _telemetrySender.MzdashContent = mzdashContent;
            _telemetrySender.MzdashName = mzdashName;

            // Advertise our built-in dashboard library to the wheel on session
            // 0x09. Wheel echoes these names in its next configJson state blob
            // (seen in usb-capture/latestcaps); PitHouse reads them for UI
            // filtering. List matches the plugin's built-in profile set.
            var libraryNames = new System.Collections.Generic.List<string>();
            foreach (var p in DashProfileStore.BuiltinProfiles)
                libraryNames.Add(p.Name);
            if (!string.IsNullOrEmpty(mzdashName) && !libraryNames.Contains(mzdashName))
                libraryNames.Add(mzdashName);
            _telemetrySender.CanonicalDashboardList = libraryNames;
        }

        /// <summary>
        /// Per-frame resolver for channels with a user-mapped SimHubProperty.
        /// Paths starting with <c>@internal/</c> are plugin-computed values
        /// (e.g. live wheel angle from the HID reader) and bypass SimHub.
        /// All other paths resolve via <c>PluginManager.GetPropertyValue</c>.
        /// </summary>
        private double ResolvePropertyAsDouble(string path)
        {
            if (!string.IsNullOrEmpty(path) && path.StartsWith("@internal/", StringComparison.Ordinal))
                return ResolveInternalChannel(path);

            return PropertyCoercion.Coerce(
                _pluginManager?.GetPropertyValue(path), path);
        }

        private double ResolveInternalChannel(string path)
        {
            switch (path)
            {
                case "@internal/SteeringWheelAngle":
                {
                    // Live wheel angle in degrees, centred at 0. Uses the base's
                    // reported max-angle (half-range ± maxAngleDeg/2). Falls back
                    // to 0 when HID is disconnected or max angle hasn't been read.
                    var hid = _hidReader;
                    int maxAngleDeg = _data?.MaxAngle * 2 ?? 0;
                    if (hid == null || maxAngleDeg <= 0) return 0.0;
                    return hid.GetCurrentAngleDegrees(maxAngleDeg);
                }
                default:
                    return 0.0;
            }
        }

        /// <summary>Stable identity key for the currently-loaded dashboard.</summary>
        internal string CurrentDashboardKey()
        {
            var profile = _telemetrySender?.Profile;
            if (profile == null) return "";
            return DashboardProfileStore.GetDashboardKey(
                _settings.TelemetryMzdashPath, profile);
        }

        /// <summary>Set or clear a per-channel SimHub property override for the current dashboard.</summary>
        internal void SetChannelMapping(string channelUrl, string propertyPath)
        {
            if (string.IsNullOrEmpty(channelUrl)) return;
            string key = CurrentDashboardKey();
            if (string.IsNullOrEmpty(key)) return;

            _settings.TelemetryChannelMappings ??=
                new System.Collections.Generic.Dictionary<string,
                    System.Collections.Generic.Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            if (!_settings.TelemetryChannelMappings.TryGetValue(key, out var inner))
            {
                inner = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _settings.TelemetryChannelMappings[key] = inner;
            }

            string trimmed = (propertyPath ?? "").Trim();
            if (string.IsNullOrEmpty(trimmed))
                inner.Remove(channelUrl);
            else
                inner[channelUrl] = trimmed;

            SaveSettings();
        }

        /// <summary>Clear all per-channel overrides for the currently-loaded dashboard.</summary>
        internal void ClearCurrentDashboardMappings()
        {
            string key = CurrentDashboardKey();
            if (string.IsNullOrEmpty(key)) return;
            if (_settings.TelemetryChannelMappings != null &&
                _settings.TelemetryChannelMappings.Remove(key))
            {
                SaveSettings();
            }
        }

        /// <summary>
        /// Restart the telemetry session with current settings. Called when protocol version,
        /// flag byte mode, or other send options change in the UI.
        /// </summary>
        internal void RestartTelemetry()
        {
            if (_telemetrySender == null) return;
            _telemetrySender.Stop();
            Interlocked.Exchange(ref _telemetryStartRequested, 0);
            ApplyTelemetrySettings();
            if (_settings.TelemetryEnabled)
                StartTelemetryIfReady();
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
            if (Interlocked.CompareExchange(ref _connectingFlag, 1, 0) != 0)
                return;

            try
            {
                // If we had a wheel detected before reconnecting, reset it.
                // The serial port may have dropped during a wheel swap.
                if (_newWheelDetected || _oldWheelDetected)
                    ResetWheelDetection("Serial reconnecting — resetting wheel detection");

                if (_connection.Connect())
                {
                    _unmatched = 0;
                    SimHub.Logging.Current.Info("[Moza] Connected to MOZA device");
                    _deviceManager.ReadSettings(StatusPollCommands);
                    _deviceManager.ProbeWheelDetection();
                    _deviceManager.ReadSetting("dash-rpm-indicator-mode");
                    _deviceManager.ReadSetting("handbrake-direction");
                    _deviceManager.ReadSetting("pedals-throttle-dir");
                    _deviceManager.ReadSetting("hub-port1-power");
                }
            }
            finally
            {
                Interlocked.Exchange(ref _connectingFlag, 0);
            }
        }

        private int _wheelPollMisses;
        private const int WheelMissThreshold = 3;
        private volatile string _lastKnownWheelModel = "";

        private void ResetWheelDetection(string reason)
        {
            SimHub.Logging.Current.Info($"[Moza] {reason}");
            _telemetrySender.Stop();
            _newWheelDetected = false;
            _oldWheelDetected = false;
            _dashDetected = false;
            WheelModelInfo = null;
            Interlocked.Exchange(ref _wheelLedGroupMask, 0);
            _data.ClearWheelIdentity();
            _deviceManager.ResetWheelDetection();
            _telemetrySender.DetectedDeviceMask = 0;
            Interlocked.Exchange(ref _telemetryStartRequested, 0);
            _wheelPollMisses = 0;
            _lastKnownWheelModel = "";
        }

        private void PollStatus(object sender, ElapsedEventArgs e)
        {
            if (IsShuttingDown) return;
            if (!_connection.IsConnected) return;

            // Hot-swap detection: track whether the locked wheel is still responding
            // and periodically verify the model name hasn't changed.
            if (_newWheelDetected || _oldWheelDetected)
            {
                if (_deviceManager.WheelRespondedSinceLastPoll)
                {
                    _wheelPollMisses = 0;
                }
                else
                {
                    _wheelPollMisses++;
                    if (_wheelPollMisses >= WheelMissThreshold)
                    {
                        ResetWheelDetection(
                            $"Wheel on ID {_deviceManager.WheelDeviceId} not responding " +
                            $"({_wheelPollMisses} misses) — resetting for hot-swap");
                    }
                }
                _deviceManager.ResetWheelResponseFlag();
                _deviceManager.ReadSetting("wheel-model-name");

                // Probe other wheel IDs for hot-swap detection.
                // Handles ES → new-protocol case where the base keeps responding
                // on the locked ID (19) so miss counter never fires.
                _deviceManager.ProbeOtherWheelIds();
            }

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
            if (!_hubDetected)
                _deviceManager.ReadSetting("hub-port1-power");

            // Poll hub port status while hub is connected (read-only, no settings to save)
            if (_hubDetected)
                _deviceManager.ReadSettings(HubReadCommands);
        }

        private volatile int _unmatched;

        private void OnMessageReceived(byte[] data)
        {
            // Bail out during shutdown — serial reader thread may deliver a frame
            // after End() began detaching state. Without this, _data/_deviceManager
            // accesses below could hit a half-disposed object.
            if (IsShuttingDown) return;

            // Filter firmware debug noise before parsing/logging
            if (data.Length >= 1 && data[0] == 0x0E)
                return;

            // Filter SerialStream control frames (group 0xC3 response to 0x43,
            // payload starts with 7C/FC + 00). These are session-management
            // chunks (fc:00 session opens/acks, 7c:00 data) handled by
            // TelemetrySender's session-layer handlers — not command responses.
            // Without this, sessions 0x01..0x0E opens spam Unmatched log lines.
            if (data.Length >= 4 && data[0] == 0xC3 &&
                (data[2] == 0x7C || data[2] == 0xFC) && data[3] == 0x00)
                return;

            // Filter wheel's `7c:23` dashboard-activate advertisements (group
            // 0xC3 device 0x71, payload starts with `7C 23`). Wheel broadcasts
            // active display config periodically — informational, not a command
            // response. Absorbed by TelemetrySender.
            if (data.Length >= 4 && data[0] == 0xC3 && data[2] == 0x7C && data[3] == 0x23)
                return;

            // Filter group 0x40 channel-config burst echoes (0xC0 response):
            //   1E 00 XX / 1E 01 XX — channel enable read per page
            //   28 00 / 28 01 / 28 02 — WheelGetCfg_GetMultiFunction{Switch,Num,Left}
            // Part of the channel configuration burst; wheel returns the stored
            // EEPROM value per channel/query. Not actionable at plugin level —
            // just confirms the probe landed. Mark wheel alive so watchdog
            // doesn't reset detection.
            if (data.Length >= 4 && data[0] == 0xC0 && data[1] == 0x71 &&
                (data[2] == 0x1E || data[2] == 0x28))
            {
                _deviceManager.MarkWheelResponse(MozaProtocol.SwapNibbles(data[1]));
                return;
            }

            var result = MozaResponseParser.Parse(data);
            if (!result.HasValue)
            {
                // Known wheel write echoes that have no command DB entry: silently
                // treat as a keepalive from the wheel device id. Avoids unmatched-log
                // spam and keeps wheel-alive tracking accurate for LED/page-config
                // writes that firmware echoes verbatim (see MozaProtocol.WheelEchoPrefixes).
                if (MozaProtocol.IsWheelEcho(data))
                {
                    _deviceManager.MarkWheelResponse(MozaProtocol.SwapNibbles(data[1]));
                    return;
                }

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

            // Normalize stick-mode: old firmware sends 2-byte value (0 or 256),
            // new firmware sends 1-byte enum (0=none, 1=left, 2=right, 3=both).
            if (r.Name == "wheel-stick-mode")
            {
                if (r.PayloadLength <= 1)
                {
                    _data.WheelDualStickSupported = true;
                }
                else
                {
                    // Old 2-byte format: 0x0100 (256) = left D-pad on
                    r.IntValue = r.IntValue >= 256 ? 1 : 0;
                }
            }

            _data.UpdateFromCommand(r.Name, r.IntValue);
            if (r.ArrayValue != null)
                _data.UpdateFromArray(r.Name, r.ArrayValue);

            // Extended LED group presence — any response to groupN brightness/mode/color
            // from this wheel proves the group exists in firmware.
            if (r.Name != null && r.Name.StartsWith("wheel-group", StringComparison.Ordinal))
            {
                int g = r.Name.Length > 11 ? r.Name[11] - '0' : -1;
                if (g >= 2 && g <= 4)
                {
                    int bit = 1 << g;
                    int prev;
                    do
                    {
                        prev = _wheelLedGroupMask;
                        if ((prev & bit) != 0) break;
                    } while (Interlocked.CompareExchange(ref _wheelLedGroupMask, prev | bit, prev) != prev);
                    if ((prev & bit) == 0)
                        SimHub.Logging.Current.Info($"[Moza] Wheel LED group {g} detected");
                }
            }

            _deviceManager.MarkWheelResponse(r.DeviceId);
            if (r.Name != null)
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
            // wheel-mcu-uid response starts with 0xBE... which parses to a negative
            // int32 via ParseIntValue(BE). Log it before the `value < 0` guard
            // below, because UpdateFromArray has already stored the raw 12 bytes.
            if (commandName == "wheel-mcu-uid" && _data.WheelMcuUid.Length > 0)
            {
                SimHub.Logging.Current.Info(
                    $"[Moza] Wheel MCU UID ({_data.WheelMcuUid.Length}B): " +
                    BitConverter.ToString(_data.WheelMcuUid).Replace("-", ""));
                return;
            }
            if (commandName == "display-mcu-uid" && _data.DisplayMcuUid.Length > 0)
            {
                SimHub.Logging.Current.Info(
                    $"[Moza] Display MCU UID ({_data.DisplayMcuUid.Length}B): " +
                    BitConverter.ToString(_data.DisplayMcuUid).Replace("-", ""));
                return;
            }

            if (value < 0) return; // No valid response

            // Update telemetry sender's heartbeat mask so it only pings detected devices
            if (deviceId >= 18 && deviceId <= 30)
                _telemetrySender.DetectedDeviceMask |= (1 << (deviceId - 18));

            // Base detection: IsBaseConnected was just set to true by UpdateFromCommand.
            // Re-apply the profile so base settings (FFB, damper, limit, etc.) are written to device.
            if (commandName == "base-mcu-temp" && !_baseDetected)
            {
                _baseDetected = true;
                SimHub.Logging.Current.Info("[Moza] Base detected");
                // Apply profile first (queues writes), then read settings (queues reads).
                // Since the write queue is FIFO, the device processes writes before reads,
                // so read responses reflect the values we just wrote.
                var profile = _settings.ProfileStore.CurrentProfile;
                if (profile != null)
                    ApplyProfile(profile);
                _deviceManager.ReadSettings(BaseSettingsReadCommands);
            }

            switch (commandName)
            {
                case "dash-rpm-indicator-mode":
                    if (!_dashDetected)
                    {
                        _dashDetected = true;
                        DeployDeviceDefinition("MOZA Dashboard", "MozaPlugin.Devices.Dash.device.json");
                        ApplySavedDashSettings();
                        _deviceManager.ReadSettings(DashSettingsReadCommands);
                        SimHub.Logging.Current.Info("[Moza] Dashboard detected");
                    }
                    break;

                case "wheel-telemetry-mode":
                    if (!_newWheelDetected && !_oldWheelDetected)
                    {
                        _newWheelDetected = true;
                        _deviceManager.LockWheelId(deviceId);
                        // Writes first so device has saved values before reads return
                        ApplySavedWheelSettings();
                        _deviceManager.ReadSetting("wheel-model-name");
                        _deviceManager.ReadSetting("wheel-sw-version");
                        _deviceManager.ReadSetting("wheel-hw-version");
                        _deviceManager.ReadSetting("wheel-serial-a");
                        _deviceManager.ReadSetting("wheel-serial-b");
                        // Match PitHouse's full 12-frame identity handshake (adds the 7 probes
                        // ReadSetting doesn't cover: 0x09/0x02/0x04/0x05/0x06/0x08-sub2/0x11).
                        _deviceManager.SendPithouseIdentityProbe(deviceId);
                        _deviceManager.ReadSettingsPaced(NewWheelSettingsReadCommands);
                        SimHub.Logging.Current.Info($"[Moza] New-protocol wheel detected on ID {deviceId}");
                        StartTelemetryIfReady();
                    }
                    else if (deviceId != _deviceManager.WheelDeviceId)
                    {
                        // Hot-swap: a wheel responded on a different ID than the locked one.
                        ResetWheelDetection(
                            $"New wheel responded on ID {deviceId} (was locked on " +
                            $"{_deviceManager.WheelDeviceId}) — hot-swap detected");
                    }
                    break;

                case "wheel-model-name":
                    // Only resolve per-model LED config for new-protocol wheels.
                    // ES wheels share device 0x13 with the base, so the model name
                    // response is the base name, not the wheel name.
                    if (_newWheelDetected)
                    {
                        var currentModel = _data.WheelModelName;

                        // Ignore empty/truncated responses — would falsely trigger reset.
                        if (string.IsNullOrEmpty(currentModel))
                            break;

                        // Hot-swap: if model name changed, a different wheel was attached.
                        if (!string.IsNullOrEmpty(_lastKnownWheelModel) &&
                            _lastKnownWheelModel != currentModel)
                        {
                            ResetWheelDetection(
                                $"Wheel model changed from '{_lastKnownWheelModel}' " +
                                $"to '{currentModel}' — hot-swap detected");
                            break;
                        }

                        // First time seeing this model — resolve LED layout and deploy
                        if (string.IsNullOrEmpty(_lastKnownWheelModel))
                        {
                            _lastKnownWheelModel = currentModel;
                            WheelModelInfo = Devices.WheelModelInfo.FromModelName(currentModel);
                            SimHub.Logging.Current.Info(
                                $"[Moza] Wheel model: {currentModel} " +
                                $"(rpm={WheelModelInfo.RpmLedCount}, buttons={WheelModelInfo.ButtonLedCount}, flags={WheelModelInfo.HasFlagLeds}, knobs={WheelModelInfo.KnobCount})");
                            // Load this wheel model's persisted slot (brightness/modes/inputs)
                            // into the active flat fields before any hardware writes fire.
                            // Seeds a new slot from current flat values on first encounter.
                            if (_settings.PerWheelSlots.ContainsKey(currentModel))
                                _settings.LoadSlotIntoActive(currentModel);
                            else
                                _settings.MirrorActiveToSlot(currentModel);
                            DeployDeviceDefinitionForModel(currentModel);

                            // Refresh _data knob colours from the slot we just loaded
                            // and push to hardware (W17/W18 only — KnobCount gate).
                            MozaProfile.UnpackColorsInto(_settings.WheelKnobBackgroundColors, _data.WheelKnobBackgroundColors);
                            MozaProfile.UnpackColorsInto(_settings.WheelKnobPrimaryColors,    _data.WheelKnobPrimaryColors);
                            WriteKnobColors(_settings.WheelKnobBackgroundColors, _settings.WheelKnobPrimaryColors);
                        }
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

                case "wheel-hw-sub":
                    if (!string.IsNullOrEmpty(_data.WheelHwSubVersion))
                        SimHub.Logging.Current.Info($"[Moza] Wheel HW sub: {_data.WheelHwSubVersion}");
                    break;

                case "wheel-mcu-uid":
                    if (_data.WheelMcuUid.Length > 0)
                        SimHub.Logging.Current.Info(
                            $"[Moza] Wheel MCU UID ({_data.WheelMcuUid.Length}B): " +
                            BitConverter.ToString(_data.WheelMcuUid).Replace("-", ""));
                    break;

                case "wheel-device-type":
                    if (_data.WheelDeviceType.Length > 0)
                        SimHub.Logging.Current.Info(
                            $"[Moza] Wheel device type: {BitConverter.ToString(_data.WheelDeviceType)}");
                    break;

                case "wheel-capabilities":
                    if (_data.WheelCapabilities.Length > 0)
                        SimHub.Logging.Current.Info(
                            $"[Moza] Wheel capabilities: {BitConverter.ToString(_data.WheelCapabilities)}");
                    break;

                case "wheel-presence":
                    SimHub.Logging.Current.Info(
                        $"[Moza] Wheel presence/ready: sub_device_count={_data.WheelSubDeviceCount}");
                    break;

                case "wheel-device-presence":
                    SimHub.Logging.Current.Info(
                        $"[Moza] Wheel device presence byte: 0x{_data.WheelDevicePresence:X2}");
                    break;

                case "wheel-identity-11":
                    if (_data.WheelIdentity11.Length > 0)
                        SimHub.Logging.Current.Info(
                            $"[Moza] Wheel identity-11: {BitConverter.ToString(_data.WheelIdentity11)}");
                    break;

                // Display sub-device identity responses (wrapped via 0x43)
                case "display-model-name":
                    if (!string.IsNullOrEmpty(_data.DisplayModelName))
                        SimHub.Logging.Current.Info($"[Moza] Display model: {_data.DisplayModelName}");
                    break;
                case "display-hw-version":
                    if (!string.IsNullOrEmpty(_data.DisplayHwVersion))
                        SimHub.Logging.Current.Info($"[Moza] Display HW: {_data.DisplayHwVersion}");
                    break;
                case "display-sw-version":
                    if (!string.IsNullOrEmpty(_data.DisplaySwVersion))
                        SimHub.Logging.Current.Info($"[Moza] Display FW: {_data.DisplaySwVersion}");
                    break;
                case "display-serial":
                    if (!string.IsNullOrEmpty(_data.DisplaySerialNumber))
                        SimHub.Logging.Current.Info($"[Moza] Display serial: {_data.DisplaySerialNumber}");
                    break;
                case "display-presence":
                    SimHub.Logging.Current.Info(
                        $"[Moza] Display presence/ready: sub_device_count={_data.DisplaySubDeviceCount}");
                    break;
                case "display-device-presence":
                    SimHub.Logging.Current.Info(
                        $"[Moza] Display device presence byte: 0x{_data.DisplayDevicePresence:X2}");
                    break;
                case "display-device-type":
                    if (_data.DisplayDeviceType.Length > 0)
                        SimHub.Logging.Current.Info(
                            $"[Moza] Display device type: {BitConverter.ToString(_data.DisplayDeviceType)}");
                    break;
                case "display-capabilities":
                    if (_data.DisplayCapabilities.Length > 0)
                        SimHub.Logging.Current.Info(
                            $"[Moza] Display capabilities: {BitConverter.ToString(_data.DisplayCapabilities)}");
                    break;
                case "display-identity-11":
                    if (_data.DisplayIdentity11.Length > 0)
                        SimHub.Logging.Current.Info(
                            $"[Moza] Display identity-11: {BitConverter.ToString(_data.DisplayIdentity11)}");
                    break;
                case "display-mcu-uid":
                    // Logged before value<0 guard (see top of DetectDevices). Not hit here.
                    break;

                case "wheel-rpm-value1":
                    if (!_newWheelDetected && !_oldWheelDetected)
                    {
                        _oldWheelDetected = true;
                        _deviceManager.LockWheelId(deviceId);
                        ApplySavedWheelSettings();
                        _deviceManager.ReadSetting("wheel-model-name");
                        _deviceManager.ReadSetting("wheel-sw-version");
                        _deviceManager.ReadSetting("wheel-hw-version");
                        _deviceManager.ReadSetting("wheel-serial-a");
                        _deviceManager.ReadSetting("wheel-serial-b");
                        _deviceManager.SendPithouseIdentityProbe(deviceId);
                        _deviceManager.ReadSettingsPaced(OldWheelSettingsReadCommands);
                        DeployDeviceDefinitionForOldProto();
                        SimHub.Logging.Current.Info($"[Moza] Old-protocol wheel detected on ID {deviceId}");
                        StartTelemetryIfReady();
                    }
                    else if (deviceId != _deviceManager.WheelDeviceId)
                    {
                        ResetWheelDetection(
                            $"New wheel responded on ID {deviceId} (was locked on " +
                            $"{_deviceManager.WheelDeviceId}) — hot-swap detected");
                    }
                    break;

                case "handbrake-direction":
                    if (!_handbrakeDetected)
                    {
                        _handbrakeDetected = true;
                        ApplySavedHandbrakeSettings();
                        _deviceManager.ReadSettings(HandbrakeSettingsReadCommands);
                        SimHub.Logging.Current.Info("[Moza] Handbrake detected");
                    }
                    break;

                case "pedals-throttle-dir":
                    if (!_pedalsDetected)
                    {
                        _pedalsDetected = true;
                        ApplySavedPedalSettings();
                        _deviceManager.ReadSettings(PedalsSettingsReadCommands);
                        SimHub.Logging.Current.Info("[Moza] Pedals detected");
                    }
                    break;

                case "hub-port1-power":
                    if (!_hubDetected)
                    {
                        _hubDetected = true;
                        if (_telemetrySender != null) _telemetrySender.HubPresent = true;
                        _deviceManager.ReadSettings(HubReadCommands);
                        SimHub.Logging.Current.Info("[Moza] Universal Hub detected");
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

            // Input settings — preload from saved values so the UI shows the
            // last-known state even when the wheel silently ignores the read
            // (newer KS firmware doesn't respond to clutch-point / knob-mode).
            if (_settings.WheelPaddlesMode >= 0) _data.WheelPaddlesMode = _settings.WheelPaddlesMode;
            if (_settings.WheelClutchPoint >= 0) _data.WheelClutchPoint = _settings.WheelClutchPoint;
            if (_settings.WheelKnobMode    >= 0) _data.WheelKnobMode    = _settings.WheelKnobMode;
            if (_settings.WheelStickMode   >= 0) _data.WheelStickMode   = _settings.WheelStickMode;

            // Knob ring colors — write-only on the wire so the only persisted copy is here.
            // Unpack now so the UI picker reflects the saved colors even before the model
            // is resolved; hardware push happens in the wheel-model-name handler once we
            // know the wheel actually exposes knob rings.
            MozaProfile.UnpackColorsInto(_settings.WheelKnobBackgroundColors, _data.WheelKnobBackgroundColors);
            MozaProfile.UnpackColorsInto(_settings.WheelKnobPrimaryColors,    _data.WheelKnobPrimaryColors);

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
            // Flag brightness routes to the Meter sub-device via dash-flags-brightness
            // (RS21 DB: MeterSetCfg_SetFlagGroupBrightness_o). Only write when the dash
            // sub-device has responded; otherwise the write targets a device that's not present.
            if (_dashDetected)
                _deviceManager.WriteSetting("dash-flags-brightness", _settings.WheelFlagsBrightness);
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

            // Enable flag indicator mode (0=Off, 1=Flags, 2=On). Firmware default is 0,
            // which silently drops all flag colour/bitmask writes. Set to 1 so the plugin's
            // bitmask-driven LEDs actually display. Subsequent read of dash-flags-indicator-mode
            // (via DashSettingsReadCommands) refreshes _data for the UI combo.
            _deviceManager.WriteSetting("dash-flags-indicator-mode", 1);
        }

        /// <summary>
        /// Apply saved handbrake settings from the current profile after detection.
        /// Previously handbrake settings were only written if the handbrake was
        /// already detected when ApplyProfile ran — now they're written on detection.
        /// </summary>
        private void ApplySavedHandbrakeSettings()
        {
            var profile = _settings.ProfileStore.CurrentProfile;
            if (profile == null) return;
            SimHub.Logging.Current.Info("[Moza] Applying saved handbrake settings");

            if (profile.HandbrakeMode >= 0)
            {
                _data.HandbrakeMode = profile.HandbrakeMode;
                _deviceManager.WriteSetting("handbrake-mode", profile.HandbrakeMode);
            }
            if (profile.HandbrakeButtonThreshold >= 0)
            {
                _data.HandbrakeButtonThreshold = profile.HandbrakeButtonThreshold;
                _deviceManager.WriteSetting("handbrake-button-threshold", profile.HandbrakeButtonThreshold);
            }
            if (profile.HandbrakeDirection >= 0)
            {
                _data.HandbrakeDirection = profile.HandbrakeDirection;
                _deviceManager.WriteSetting("handbrake-direction", profile.HandbrakeDirection);
            }
            if (profile.HandbrakeMin >= 0)
            {
                _data.HandbrakeMin = profile.HandbrakeMin;
                _deviceManager.WriteSetting("handbrake-min", profile.HandbrakeMin);
            }
            if (profile.HandbrakeMax >= 0)
            {
                _data.HandbrakeMax = profile.HandbrakeMax;
                _deviceManager.WriteSetting("handbrake-max", profile.HandbrakeMax);
            }
            if (profile.HandbrakeCurve != null)
            {
                for (int i = 0; i < Math.Min(5, profile.HandbrakeCurve.Length); i++)
                {
                    _data.HandbrakeCurve[i] = profile.HandbrakeCurve[i];
                    _deviceManager.WriteFloat($"handbrake-y{i + 1}", profile.HandbrakeCurve[i]);
                }
            }
        }

        /// <summary>
        /// Apply saved pedal settings from the current profile after detection.
        /// </summary>
        private void ApplySavedPedalSettings()
        {
            var profile = _settings.ProfileStore.CurrentProfile;
            if (profile == null) return;
            SimHub.Logging.Current.Info("[Moza] Applying saved pedal settings");

            if (profile.PedalsThrottleDir >= 0)
            {
                _data.PedalsThrottleDir = profile.PedalsThrottleDir;
                _deviceManager.WriteSetting("pedals-throttle-dir", profile.PedalsThrottleDir);
            }
            if (profile.PedalsBrakeDir >= 0)
            {
                _data.PedalsBrakeDir = profile.PedalsBrakeDir;
                _deviceManager.WriteSetting("pedals-brake-dir", profile.PedalsBrakeDir);
            }
            if (profile.PedalsClutchDir >= 0)
            {
                _data.PedalsClutchDir = profile.PedalsClutchDir;
                _deviceManager.WriteSetting("pedals-clutch-dir", profile.PedalsClutchDir);
            }
            if (profile.PedalsBrakeAngleRatio >= 0)
            {
                _data.PedalsBrakeAngleRatio = profile.PedalsBrakeAngleRatio;
                _deviceManager.WriteFloat("pedals-brake-angle-ratio", profile.PedalsBrakeAngleRatio);
            }
            ApplyCurveIfSet(profile.PedalsThrottleCurve, _data.PedalsThrottleCurve, "pedals-throttle-y", true);
            ApplyCurveIfSet(profile.PedalsBrakeCurve, _data.PedalsBrakeCurve, "pedals-brake-y", true);
            ApplyCurveIfSet(profile.PedalsClutchCurve, _data.PedalsClutchCurve, "pedals-clutch-y", true);
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

            // Detach any prior subscription before re-subscribing (ClearSettings
            // replaces _settings, leaving the old store with a stale handler that
            // would otherwise fire and mutate the new state via `this`).
            if (_subscribedProfileStore != null && !ReferenceEquals(_subscribedProfileStore, store))
                _subscribedProfileStore.CurrentProfileChanged -= OnProfileChanged;

            // Subscribe to profile changes (game switch, manual selection)
            store.CurrentProfileChanged += OnProfileChanged;
            _subscribedProfileStore = store;

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
                if (profile.WheelButtonDefaultDuringTelemetry != null)
                {
                    int n = Math.Min(profile.WheelButtonDefaultDuringTelemetry.Length, _data.WheelButtonDefaultDuringTelemetry.Length);
                    for (int i = 0; i < n; i++)
                        _data.WheelButtonDefaultDuringTelemetry[i] = profile.WheelButtonDefaultDuringTelemetry[i];
                }
                MozaProfile.UnpackColorsInto(profile.WheelFlagColors, _data.WheelFlagColors);
                if (profile.WheelIdleColor != null && profile.WheelIdleColor.Length > 0)
                {
                    var rgb = MozaProfile.UnpackColor(profile.WheelIdleColor[0]);
                    _data.WheelIdleColor[0] = rgb[0];
                    _data.WheelIdleColor[1] = rgb[1];
                    _data.WheelIdleColor[2] = rgb[2];
                }
                MozaProfile.UnpackColorsInto(profile.WheelESRpmColors, _data.WheelESRpmColors);
                MozaProfile.UnpackColorsInto(profile.WheelKnobBackgroundColors, _data.WheelKnobBackgroundColors);
                MozaProfile.UnpackColorsInto(profile.WheelKnobPrimaryColors,    _data.WheelKnobPrimaryColors);
                _settings.WheelRpmBlinkColors = profile.WheelRpmBlinkColors;
                _settings.WheelKnobBackgroundColors = profile.WheelKnobBackgroundColors;
                _settings.WheelKnobPrimaryColors    = profile.WheelKnobPrimaryColors;
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
            if (_data.IsConnected)
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
                    // Flag brightness → Meter sub-device (dash-flags-brightness). Gate on dash detected.
                    if (profile.WheelFlagsBrightness >= 0 && _dashDetected)
                        _deviceManager.WriteSetting("dash-flags-brightness", profile.WheelFlagsBrightness);
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
            // New-protocol wheel colors
            if (!DeviceExtensionActive && _newWheelDetected)
            {
                WriteColorArray(profile.WheelRpmColors, "wheel-rpm-color", 18);
                WriteColorArray(profile.WheelRpmBlinkColors, "wheel-rpm-blink-color", 10);
                WriteColorArray(profile.WheelButtonColors, "wheel-button-color", 14);
                // Flag colors route to Meter sub-device via dash-flag-color*. Gate on dash detection.
                if (_dashDetected)
                    WriteColorArray(profile.WheelFlagColors, "dash-flag-color", 6);
                if (profile.WheelIdleColor != null && profile.WheelIdleColor.Length > 0)
                {
                    var rgb = MozaProfile.UnpackColor(profile.WheelIdleColor[0]);
                    _deviceManager.WriteColor("wheel-idle-color", rgb[0], rgb[1], rgb[2]);
                }
                WriteKnobColors(profile.WheelKnobBackgroundColors, profile.WheelKnobPrimaryColors);
            }

            // Old-protocol (ES) wheel colors
            if (!DeviceExtensionActive && _oldWheelDetected)
            {
                WriteColorArray(profile.WheelESRpmColors, "wheel-old-rpm-color", 10);
            }

            // Dash colors
            if (!DashDeviceExtensionActive && _dashDetected)
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
        /// Push per-knob background + primary colors to the wheel. No-op unless the
        /// active wheel model exposes knob LED rings (W17 CS Pro / W18 KS Pro).
        /// Source arrays are packed R&lt;&lt;16|G&lt;&lt;8|B per knob; null = skip.
        /// </summary>
        private void WriteKnobColors(int[]? packedBackground, int[]? packedPrimary)
        {
            int knobs = WheelModelInfo?.KnobCount ?? 0;
            if (knobs <= 0) return;
            WriteKnobRoleArray(packedBackground, "bg-color", knobs);
            WriteKnobRoleArray(packedPrimary,    "primary-color", knobs);
        }

        private void WriteKnobRoleArray(int[]? packedColors, string roleSuffix, int count)
        {
            if (packedColors == null) return;
            int len = Math.Min(packedColors.Length, count);
            for (int i = 0; i < len; i++)
            {
                var rgb = MozaProfile.UnpackColor(packedColors[i]);
                _deviceManager.WriteColor($"wheel-knob{i + 1}-{roleSuffix}", rgb[0], rgb[1], rgb[2]);
            }
        }

        /// <summary>
        /// Apply wheel settings from the SimHub device extension profile system.
        /// Updates _settings, _data, and writes to hardware if connected.
        /// </summary>
        internal void ApplyWheelExtensionSettings(MozaWheelExtensionSettings extSettings)
        {
            SimHub.Logging.Current.Info("[Moza] Applying wheel device extension settings");

            // Update _settings and _data in-memory. ApplyTo already routes into
            // the correct per-model slot and only updates flat fields when this
            // extension's captured model matches the currently-connected wheel.
            extSettings.ApplyTo(_settings, _data);

            // Gate hardware writes + _data mutations on model match — extensions
            // for other wheel models must not poke the active wheel's hardware.
            string extModel = extSettings.WheelModelName ?? "";
            string activeModel = _data.WheelModelName ?? "";
            bool hasExtModel = !string.IsNullOrEmpty(extModel);
            bool modelMatches = hasExtModel &&
                string.Equals(extModel, activeModel, StringComparison.OrdinalIgnoreCase);
            bool writeHardware = !hasExtModel || modelMatches;

            // Persist blink colors only when this extension owns the active wheel.
            if (writeHardware)
                _settings.WheelRpmBlinkColors = extSettings.WheelRpmBlinkColors;

            // Write to hardware if connected and this extension matches the active wheel
            if (writeHardware && _data.IsConnected)
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
                    // Flag brightness → Meter sub-device (dash-flags-brightness). Gate on dash detected.
                    if (extSettings.WheelFlagsBrightness >= 0 && _dashDetected)
                        _deviceManager.WriteSetting("dash-flags-brightness", extSettings.WheelFlagsBrightness);
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
                WriteColorArray(extSettings.WheelRpmColors, "wheel-rpm-color", 18);
                WriteColorArray(extSettings.WheelRpmBlinkColors, "wheel-rpm-blink-color", 10);
                WriteColorArray(extSettings.WheelButtonColors, "wheel-button-color", 14);
                // Flag colors → Meter sub-device (dash-flag-color*). Gate on dash detection.
                if (_dashDetected)
                    WriteColorArray(extSettings.WheelFlagColors, "dash-flag-color", 6);
                if (extSettings.WheelIdleColor != null && extSettings.WheelIdleColor.Length > 0)
                {
                    var rgb = MozaProfile.UnpackColor(extSettings.WheelIdleColor[0]);
                    _deviceManager.WriteColor("wheel-idle-color", rgb[0], rgb[1], rgb[2]);
                }
                WriteColorArray(extSettings.WheelESRpmColors, "wheel-old-rpm-color", 10);
                WriteKnobColors(extSettings.WheelKnobBackgroundColors, extSettings.WheelKnobPrimaryColors);
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
            if (_data.IsConnected && _dashDetected)
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

            DeployGeneratedWheelDefinition(deviceName, guid, friendlyName, modelInfo.RpmLedCount, modelInfo.HasFlagLeds, modelInfo.ButtonLedCount);
        }

        private void DeployGeneratedWheelDefinition(string deviceName, string guid, string productName, int rpmCount, bool hasFlagLeds, int buttonCount)
        {
            try
            {
                var simHubDir = AppDomain.CurrentDomain.BaseDirectory;
                var userDefsDir = Path.Combine(simHubDir, "DevicesDefinitions", "User");
                var deviceDir = Path.Combine(userDefsDir, deviceName);
                var deviceJsonPath = Path.Combine(deviceDir, "device.json");

                int expectedTelemetryCount = rpmCount + (hasFlagLeds ? 6 : 0);
                bool fileExists = File.Exists(deviceJsonPath);
                bool stale = false;

                if (fileExists)
                {
                    // Compare existing LogicalTelemetryLeds.LedCount + LogicalButtonsSection.Items
                    // against expected. Mismatch = layout changed in a plugin update; rewrite.
                    try
                    {
                        var existing = JObject.Parse(File.ReadAllText(deviceJsonPath));
                        int existingLed = existing.SelectToken("LedsFeature.LogicalTelemetryLeds.LedCount")?.Value<int>() ?? -1;
                        int existingButtons = (existing.SelectToken("LedsFeature.LogicalButtonsSection.Items") as JArray)?.Count ?? -1;
                        stale = existingLed != expectedTelemetryCount || existingButtons != buttonCount;
                    }
                    catch (Exception parseEx)
                    {
                        SimHub.Logging.Current.Warn(
                            $"[Moza] Could not parse existing device.json for '{deviceName}', rewriting: {parseEx.Message}");
                        stale = true;
                    }

                    if (!stale)
                        return;
                }

                Directory.CreateDirectory(deviceDir);

                var pid = _connection.DiscoveredPid ?? "0x0004";
                var json = GenerateWheelDeviceJson(guid, productName, rpmCount, hasFlagLeds, buttonCount, pid);
                File.WriteAllText(deviceJsonPath, json);

                DeviceDefinitionDeployed = true;
                string action = stale ? "Refreshed" : "Deployed";
                SimHub.Logging.Current.Info(
                    $"[Moza] {action} device definition: {deviceName} " +
                    $"(guid={guid}, telemetryLeds={expectedTelemetryCount}, rpm={rpmCount}, flags={hasFlagLeds}, " +
                    $"buttons={buttonCount}, pid={pid}, restart SimHub to pick up changes)");
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"[Moza] Error deploying device definition '{deviceName}': {ex.Message}");
            }
        }

        private static string GenerateWheelDeviceJson(string guid, string productName, int rpmCount, bool hasFlagLeds, int buttonCount, string pid)
        {
            var physItems = new JArray();

            // Telemetry LEDs: single contiguous sequence. When the wheel has flag LEDs
            // they are 3-on-each-side of the RPM strip, so SimHub sees (rpmCount + 6)
            // LEDs as one logical run: [flag 1..3][rpm 1..N][flag 4..6].
            int telemetryCount = rpmCount + (hasFlagLeds ? 6 : 0);
            physItems.Add(new JObject
            {
                ["SourceRole"] = 1,
                ["SourceIndex"] = 0,
                ["RepeatCount"] = telemetryCount,
                ["RepeatMode"] = 1
            });
            for (int i = 1; i < telemetryCount; i++)
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
                        ["LedCount"] = telemetryCount,
                        ["Segments"] = hasFlagLeds
                            ? new JArray(new JObject { ["Size"] = 3 })
                            : new JArray(),
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
