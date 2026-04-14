using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MozaPlugin
{
    public partial class SettingsControl : UserControl
    {
        private readonly MozaPlugin _plugin;
        private readonly MozaDeviceManager _device;
        private readonly MozaData _data;
        private readonly DispatcherTimer _refreshTimer;
        private bool _suppressEvents;

        public SettingsControl(MozaPlugin plugin)
        {
            _plugin = plugin;
            _device = plugin.DeviceManager;
            _data = plugin.Data;

            _suppressEvents = true;
            InitializeComponent();
            ConnectionToggle.IsChecked = plugin.ConnectionEnabled;
            AutoApplyProfileCheck.IsChecked = plugin.Settings.AutoApplyProfileOnLaunch;
            LimitWheelUpdatesCheck.IsChecked = plugin.Settings.LimitWheelUpdates;
            WheelKeepaliveCheck.IsChecked = plugin.Settings.WheelKeepalive;
            AlwaysResendBitmaskCheck.IsChecked = plugin.Settings.AlwaysResendBitmask;
            _suppressEvents = false;

            InitProfilesTab();

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _refreshTimer.Tick += RefreshDisplay;
            _refreshTimer.Start();

            RequestAllSettings();
        }

        private MozaPluginSettings _settings => _plugin.Settings;

        // ===== Refresh =====

        private void RequestAllSettings()
        {
            _device.ReadSettings(
                "base-limit", "base-ffb-strength", "base-torque", "base-speed",
                "base-damper", "base-friction", "base-inertia", "base-spring",
                "main-get-damper-gain", "main-get-friction-gain",
                "main-get-inertia-gain", "main-get-spring-gain",
                "base-protection", "base-natural-inertia",
                "base-speed-damping", "base-speed-damping-point",
                "base-soft-limit-stiffness", "base-soft-limit-retain",
                "base-ffb-reverse", "main-get-work-mode", "main-get-led-status",
                "main-get-ble-mode",
                "base-mcu-temp", "base-mosfet-temp", "base-motor-temp"
            );
        }

        private void RefreshDisplay(object sender, EventArgs e)
        {
            RestartBanner.Visibility = _plugin.DeviceDefinitionDeployed
                ? Visibility.Visible : Visibility.Collapsed;

            _suppressEvents = true;
            try
            {
                RefreshBaseTab();
                RefreshHandbrakeTab();
                RefreshPedalsTab();
                InitTelemetryTab();
                RefreshTelemetryStatus();
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        private void RefreshBaseTab()
        {
            ConnectionIndicator.Fill = _data.IsBaseConnected ? Brushes.LimeGreen : Brushes.Gray;
            ConnectionLabel.Text = _data.IsBaseConnected ? "Connected" : "Disconnected";

            string tempUnit = _data.UseFahrenheit ? "°F" : "°C";
            McuTempLabel.Text = _data.IsBaseConnected ? $"{ConvertTemp(_data.McuTemp):F0} {tempUnit}" : "--";
            MosfetTempLabel.Text = _data.IsBaseConnected ? $"{ConvertTemp(_data.MosfetTemp):F0} {tempUnit}" : "--";
            MotorTempLabel.Text = _data.IsBaseConnected ? $"{ConvertTemp(_data.MotorTemp):F0} {tempUnit}" : "--";

            // Reverse expression: *2 (raw → display degrees)
            double rot = _data.Limit * 2.0;
            RotationSlider.Value = Clamp(rot, 90, 2700);
            RotationValue.Text = $"{rot:F0}°";

            double ffb = _data.FfbStrength / 10.0;
            FfbStrengthSlider.Value = Clamp(ffb, 0, 100);
            FfbStrengthValue.Text = $"{ffb:F0}%";

            TorqueSlider.Value = Clamp(_data.Torque, 50, 100);
            TorqueValue.Text = $"{_data.Torque}%";

            double spd = _data.Speed / 10.0;
            SpeedSlider.Value = Clamp(spd, 0, 200);
            SpeedValue.Text = $"{spd:F0}%";

            SetSliderPercent(DamperSlider, DamperValue, _data.Damper / 10.0, 0, 100);
            SetSliderPercent(FrictionSlider, FrictionValue, _data.Friction / 10.0, 0, 100);
            InertiaSlider.Value = Clamp(_data.Inertia / 10.0, 100, 500);
            InertiaValue.Text = $"{_data.Inertia / 10.0:F0}";
            SetSliderPercent(SpringSlider, SpringValue, _data.Spring / 10.0, 0, 100);

            FfbReverseCheck.IsChecked = _data.FfbReverse != 0;

            SetSliderPercent(GameDamperSlider, GameDamperValue, _data.GameDamper / 2.55, 0, 100);
            SetSliderPercent(GameFrictionSlider, GameFrictionValue, _data.GameFriction / 2.55, 0, 100);
            SetSliderPercent(GameInertiaSlider, GameInertiaValue, _data.GameInertia / 2.55, 0, 100);
            SetSliderPercent(GameSpringSlider, GameSpringValue, _data.GameSpring / 2.55, 0, 100);

            SpeedDampingSlider.Value = Clamp(_data.SpeedDamping, 0, 100);
            SpeedDampingValue.Text = $"{_data.SpeedDamping}%";
            SpeedDampingPointSlider.Value = Clamp(_data.SpeedDampingPoint, 0, 400);
            SpeedDampingPointValue.Text = $"{_data.SpeedDampingPoint} kph";

            ProtectionCheck.IsChecked = _data.Protection != 0;
            NaturalInertiaSlider.Value = Clamp(_data.NaturalInertia, 100, 4000);
            NaturalInertiaValue.Text = $"{_data.NaturalInertia}";

            double stiff = (_data.SoftLimitStiffness / (400.0 / 9.0)) - 2.25 + 1.0;
            stiff = Math.Round(Clamp(stiff, 1, 10));
            SoftLimitStiffnessSlider.Value = stiff;
            SoftLimitStiffnessValue.Text = $"{stiff:F0}";
            SoftLimitRetainCheck.IsChecked = _data.SoftLimitRetain != 0;

            StandbyCheck.IsChecked = _data.WorkMode != 0;
            LedStatusCheck.IsChecked = _data.LedStatus != 0;
            BluetoothCheck.IsChecked = _data.BleMode == 0;

            // FFB Equalizer (0-400% where 100% is default/flat)
            SetSliderRaw(Eq1Slider, Eq1Value, _data.Equalizer1, 0, 400, "%");
            SetSliderRaw(Eq2Slider, Eq2Value, _data.Equalizer2, 0, 400, "%");
            SetSliderRaw(Eq3Slider, Eq3Value, _data.Equalizer3, 0, 400, "%");
            SetSliderRaw(Eq4Slider, Eq4Value, _data.Equalizer4, 0, 400, "%");
            SetSliderRaw(Eq5Slider, Eq5Value, _data.Equalizer5, 0, 400, "%");
            SetSliderRaw(Eq6Slider, Eq6Value, _data.Equalizer6, 0, 400, "%");

            // FFB Curve (X breakpoints are fixed at 20/40/60/80; only Y output values are user-adjustable)
            SetSliderRaw(FfbCurveY1Slider, FfbCurveY1Value, _data.FfbCurveY1, 0, 100, "");
            SetSliderRaw(FfbCurveY2Slider, FfbCurveY2Value, _data.FfbCurveY2, 0, 100, "");
            SetSliderRaw(FfbCurveY3Slider, FfbCurveY3Value, _data.FfbCurveY3, 0, 100, "");
            SetSliderRaw(FfbCurveY4Slider, FfbCurveY4Value, _data.FfbCurveY4, 0, 100, "");
            SetSliderRaw(FfbCurveY5Slider, FfbCurveY5Value, _data.FfbCurveY5, 0, 100, "");
        }

        // ===== Base tab slider handlers =====
        // Each handler writes to device AND updates _data so the refresh timer doesn't revert.

        private void RotationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int deg = (int)Math.Round(e.NewValue);
            // Expression: /2 (display degrees → raw)
            int raw = deg / 2;
            RotationValue.Text = $"{deg}°";
            _data.Limit = raw;
            _data.MaxAngle = raw;
            _device.WriteSetting("base-limit", raw);
            _device.WriteSetting("base-max-angle", raw);
            _plugin.SaveSettings();
        }

        private void FfbStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = pct * 10;
            FfbStrengthValue.Text = $"{pct}%";
            _data.FfbStrength = raw;
            _device.WriteSetting("base-ffb-strength", raw);
            _plugin.SaveSettings();
        }

        private void TorqueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            TorqueValue.Text = $"{val}%";
            _data.Torque = val;
            _device.WriteSetting("base-torque", val);
            _plugin.SaveSettings();
        }

        private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = pct * 10;
            SpeedValue.Text = $"{pct}%";
            _data.Speed = raw;
            _device.WriteSetting("base-speed", raw);
            _plugin.SaveSettings();
        }

        private void DamperSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = pct * 10;
            DamperValue.Text = $"{pct}%";
            _data.Damper = raw;
            _device.WriteSetting("base-damper", raw);
            _plugin.SaveSettings();
        }

        private void FrictionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = pct * 10;
            FrictionValue.Text = $"{pct}%";
            _data.Friction = raw;
            _device.WriteSetting("base-friction", raw);
            _plugin.SaveSettings();
        }

        private void InertiaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            int raw = val * 10;
            InertiaValue.Text = $"{val}";
            _data.Inertia = raw;
            _device.WriteSetting("base-inertia", raw);
            _plugin.SaveSettings();
        }

        private void SpringSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = pct * 10;
            SpringValue.Text = $"{pct}%";
            _data.Spring = raw;
            _device.WriteSetting("base-spring", raw);
            _plugin.SaveSettings();
        }

        private void GameDamperSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = (int)Math.Round(pct * 2.55);
            GameDamperValue.Text = $"{pct}%";
            _data.GameDamper = raw;
            _device.WriteSetting("main-set-damper-gain", raw);
            _plugin.SaveSettings();
        }

        private void GameFrictionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = (int)Math.Round(pct * 2.55);
            GameFrictionValue.Text = $"{pct}%";
            _data.GameFriction = raw;
            _device.WriteSetting("main-set-friction-gain", raw);
            _plugin.SaveSettings();
        }

        private void GameInertiaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = (int)Math.Round(pct * 2.55);
            GameInertiaValue.Text = $"{pct}%";
            _data.GameInertia = raw;
            _device.WriteSetting("main-set-inertia-gain", raw);
            _plugin.SaveSettings();
        }

        private void GameSpringSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int pct = (int)Math.Round(e.NewValue);
            int raw = (int)Math.Round(pct * 2.55);
            GameSpringValue.Text = $"{pct}%";
            _data.GameSpring = raw;
            _device.WriteSetting("main-set-spring-gain", raw);
            _plugin.SaveSettings();
        }

        private void SpeedDampingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            SpeedDampingValue.Text = $"{val}%";
            _data.SpeedDamping = val;
            _device.WriteSetting("base-speed-damping", val);
            _plugin.SaveSettings();
        }

        private void SpeedDampingPointSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            SpeedDampingPointValue.Text = $"{val} kph";
            _data.SpeedDampingPoint = val;
            _device.WriteSetting("base-speed-damping-point", val);
            _plugin.SaveSettings();
        }

        private void NaturalInertiaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            NaturalInertiaValue.Text = $"{val}";
            _data.NaturalInertia = val;
            _device.WriteSetting("base-natural-inertia", val);
            _plugin.SaveSettings();
        }

        private void SoftLimitStiffnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int display = (int)Math.Round(e.NewValue);
            int raw = (int)Math.Round(display * (400.0 / 9.0) - (400.0 / 9.0) + 100.0);
            SoftLimitStiffnessValue.Text = $"{display}";
            _data.SoftLimitStiffness = raw;
            _device.WriteSetting("base-soft-limit-stiffness", raw);
            _plugin.SaveSettings();
        }

        // ===== Checkbox handlers =====

        private void FfbReverseCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = FfbReverseCheck.IsChecked == true ? 1 : 0;
            _data.FfbReverse = val;
            _device.WriteSetting("base-ffb-reverse", val);
            _plugin.SaveSettings();
        }

        private void ProtectionCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = ProtectionCheck.IsChecked == true ? 1 : 0;
            _data.Protection = val;
            _device.WriteSetting("base-protection", val);
            _plugin.SaveSettings();
        }

        private void SoftLimitRetainCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = SoftLimitRetainCheck.IsChecked == true ? 1 : 0;
            _data.SoftLimitRetain = val;
            _device.WriteSetting("base-soft-limit-retain", val);
            _plugin.SaveSettings();
        }

        private void StandbyCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = StandbyCheck.IsChecked == true ? 1 : 0;
            _data.WorkMode = val;
            _device.WriteSetting("main-set-work-mode", val);
            _plugin.SaveSettings();
        }

        private void LedStatusCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = LedStatusCheck.IsChecked == true ? 1 : 0;
            _data.LedStatus = val;
            _device.WriteSetting("main-set-led-status", val);
            _plugin.SaveSettings();
        }

        // ===== RPM range slider handlers =====

        // ===== Handbrake tab =====

        private void RefreshHandbrakeTab()
        {
            bool detected = _plugin.IsHandbrakeDetected;
            HandbrakeTab.Visibility = detected ? Visibility.Visible : Visibility.Collapsed;

            if (!detected) return;

            SetComboSafe(HandbrakeModeCombo, _data.HandbrakeMode);

            // Show threshold slider only in button mode
            HandbrakeThresholdPanel.Visibility = _data.HandbrakeMode == 1
                ? Visibility.Visible : Visibility.Collapsed;

            HandbrakeThresholdSlider.Value = Clamp(_data.HandbrakeButtonThreshold, 0, 100);
            HandbrakeThresholdValue.Text = $"{_data.HandbrakeButtonThreshold}%";

            HandbrakeDirectionCheck.IsChecked = _data.HandbrakeDirection != 0;

            HandbrakeMinSlider.Value = Clamp(_data.HandbrakeMin, 0, 100);
            HandbrakeMinValue.Text = $"{_data.HandbrakeMin}%";
            HandbrakeMaxSlider.Value = Clamp(_data.HandbrakeMax, 0, 100);
            HandbrakeMaxValue.Text = $"{_data.HandbrakeMax}%";

            SetSliderRaw(HbY1Slider, HbY1Value, _data.HandbrakeCurve[0], 0, 100, "");
            SetSliderRaw(HbY2Slider, HbY2Value, _data.HandbrakeCurve[1], 0, 100, "");
            SetSliderRaw(HbY3Slider, HbY3Value, _data.HandbrakeCurve[2], 0, 100, "");
            SetSliderRaw(HbY4Slider, HbY4Value, _data.HandbrakeCurve[3], 0, 100, "");
            SetSliderRaw(HbY5Slider, HbY5Value, _data.HandbrakeCurve[4], 0, 100, "");
        }

        private void HandbrakeModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = HandbrakeModeCombo.SelectedIndex;
            _data.HandbrakeMode = val;
            _device.WriteSetting("handbrake-mode", val);
            _plugin.SaveSettings();
        }

        private void HandbrakeThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            HandbrakeThresholdValue.Text = $"{val}%";
            _data.HandbrakeButtonThreshold = val;
            _device.WriteSetting("handbrake-button-threshold", val);
            _plugin.SaveSettings();
        }

        private void HandbrakeDirectionCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = HandbrakeDirectionCheck.IsChecked == true ? 1 : 0;
            _data.HandbrakeDirection = val;
            _device.WriteSetting("handbrake-direction", val);
            _plugin.SaveSettings();
        }

        // ===== Connection toggle =====

        private void ConnectionToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.SetConnectionEnabled(ConnectionToggle.IsChecked == true);
        }

        // ===== Refresh button =====

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RequestAllSettings();
        }

        // ===== Helpers =====

        private void SetSliderPercent(Slider slider, TextBlock label, double value, double min, double max)
        {
            slider.Value = Clamp(value, min, max);
            label.Text = $"{value:F0}%";
        }

        private static void SetComboSafe(ComboBox combo, int index)
        {
            if (index >= 0 && index < combo.Items.Count)
                combo.SelectedIndex = index;
        }

        private double ConvertTemp(int raw)
        {
            double celsius = raw / 100.0;
            return _data.UseFahrenheit ? celsius * 9.0 / 5.0 + 32.0 : celsius;
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        // ===== Helpers (new) =====

        private void SetSliderRaw(Slider slider, TextBlock label, int value, int min, int max, string suffix)
        {
            slider.Value = Clamp(value, min, max);
            label.Text = $"{value}{suffix}";
        }

        // ===== FFB Equalizer handlers =====

        private static readonly string[] EqCommands = {
            "base-equalizer1", "base-equalizer2", "base-equalizer3",
            "base-equalizer4", "base-equalizer5", "base-equalizer6"
        };

        private void WriteEq(int index, int value)
        {
            _device.WriteSetting(EqCommands[index], value);
            _plugin.SaveSettings();
        }

        private void Eq1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); Eq1Value.Text = $"{v}%"; _data.Equalizer1 = v; WriteEq(0, v); }
        private void Eq2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); Eq2Value.Text = $"{v}%"; _data.Equalizer2 = v; WriteEq(1, v); }
        private void Eq3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); Eq3Value.Text = $"{v}%"; _data.Equalizer3 = v; WriteEq(2, v); }
        private void Eq4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); Eq4Value.Text = $"{v}%"; _data.Equalizer4 = v; WriteEq(3, v); }
        private void Eq5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); Eq5Value.Text = $"{v}%"; _data.Equalizer5 = v; WriteEq(4, v); }
        private void Eq6Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); Eq6Value.Text = $"{v}%"; _data.Equalizer6 = v; WriteEq(5, v); }

        // ===== FFB Curve handlers =====

        // Presets: [Y1, Y2, Y3, Y4, Y5] — X breakpoints are fixed at [20, 40, 60, 80]
        private static readonly int[][] FfbCurvePresets =
        {
            new[] { 20, 40, 60, 80, 100 }, // Linear
            new[] {  8, 24, 76, 92, 100 }, // S-Curve
            new[] {  6, 14, 28, 54, 100 }, // Exponential
            new[] { 46, 72, 86, 94, 100 }, // Parabolic
        };

        private void ApplyFfbCurvePreset(int[] p)
        {
            _suppressEvents = true;
            FfbCurveY1Slider.Value = p[0]; FfbCurveY1Value.Text = $"{p[0]}"; _data.FfbCurveY1 = p[0];
            FfbCurveY2Slider.Value = p[1]; FfbCurveY2Value.Text = $"{p[1]}"; _data.FfbCurveY2 = p[1];
            FfbCurveY3Slider.Value = p[2]; FfbCurveY3Value.Text = $"{p[2]}"; _data.FfbCurveY3 = p[2];
            FfbCurveY4Slider.Value = p[3]; FfbCurveY4Value.Text = $"{p[3]}"; _data.FfbCurveY4 = p[3];
            FfbCurveY5Slider.Value = p[4]; FfbCurveY5Value.Text = $"{p[4]}"; _data.FfbCurveY5 = p[4];
            _suppressEvents = false;
            // Always write fixed X breakpoints first
            _device.WriteSetting("base-ffb-curve-x1", 20); _device.WriteSetting("base-ffb-curve-x2", 40);
            _device.WriteSetting("base-ffb-curve-x3", 60); _device.WriteSetting("base-ffb-curve-x4", 80);
            _device.WriteSetting("base-ffb-curve-y1", p[0]); _device.WriteSetting("base-ffb-curve-y2", p[1]);
            _device.WriteSetting("base-ffb-curve-y3", p[2]); _device.WriteSetting("base-ffb-curve-y4", p[3]);
            _device.WriteSetting("base-ffb-curve-y5", p[4]);
            _plugin.SaveSettings();
        }

        private void FfbCurvePreset_Linear(object s, RoutedEventArgs e) => ApplyFfbCurvePreset(FfbCurvePresets[0]);
        private void FfbCurvePreset_SCurve(object s, RoutedEventArgs e) => ApplyFfbCurvePreset(FfbCurvePresets[1]);
        private void FfbCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyFfbCurvePreset(FfbCurvePresets[2]);
        private void FfbCurvePreset_Parabolic(object s, RoutedEventArgs e) => ApplyFfbCurvePreset(FfbCurvePresets[3]);

        private void FfbCurveY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); FfbCurveY1Value.Text = $"{v}"; _data.FfbCurveY1 = v; _device.WriteSetting("base-ffb-curve-y1", v); _plugin.SaveSettings(); }
        private void FfbCurveY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); FfbCurveY2Value.Text = $"{v}"; _data.FfbCurveY2 = v; _device.WriteSetting("base-ffb-curve-y2", v); _plugin.SaveSettings(); }
        private void FfbCurveY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); FfbCurveY3Value.Text = $"{v}"; _data.FfbCurveY3 = v; _device.WriteSetting("base-ffb-curve-y3", v); _plugin.SaveSettings(); }
        private void FfbCurveY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); FfbCurveY4Value.Text = $"{v}"; _data.FfbCurveY4 = v; _device.WriteSetting("base-ffb-curve-y4", v); _plugin.SaveSettings(); }
        private void FfbCurveY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); FfbCurveY5Value.Text = $"{v}"; _data.FfbCurveY5 = v; _device.WriteSetting("base-ffb-curve-y5", v); _plugin.SaveSettings(); }

        // ===== Bluetooth + Base Calibration =====

        private void BluetoothCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = BluetoothCheck.IsChecked == true ? 0 : 85;
            _data.BleMode = val;
            _device.WriteSetting("main-set-ble-mode", val);
            _plugin.SaveSettings();
        }

        private void BaseCalibrateButton_Click(object sender, RoutedEventArgs e)
        {
            _device.WriteSetting("base-calibration", 1);
            BaseCalibrateStatus.Text = "Calibration sent";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (s, _) => { BaseCalibrateStatus.Text = ""; ((DispatcherTimer)s!).Stop(); };
            timer.Start();
        }

        // ===== Handbrake Range + Curve + Calibration =====

        private static readonly int[][] HbCurvePresets =
        {
            new[] { 20, 40,  60,  80, 100 }, // Linear
            new[] {  8, 24,  76,  92, 100 }, // S Curve
            new[] {  6, 14,  28,  54, 100 }, // Exponential
            new[] { 46, 72,  86,  94, 100 }, // Parabolic
        };

        private void ApplyHbCurvePreset(int[] p)
        {
            _suppressEvents = true;
            HbY1Slider.Value = p[0]; HbY1Value.Text = $"{p[0]}"; _data.HandbrakeCurve[0] = p[0];
            HbY2Slider.Value = p[1]; HbY2Value.Text = $"{p[1]}"; _data.HandbrakeCurve[1] = p[1];
            HbY3Slider.Value = p[2]; HbY3Value.Text = $"{p[2]}"; _data.HandbrakeCurve[2] = p[2];
            HbY4Slider.Value = p[3]; HbY4Value.Text = $"{p[3]}"; _data.HandbrakeCurve[3] = p[3];
            HbY5Slider.Value = p[4]; HbY5Value.Text = $"{p[4]}"; _data.HandbrakeCurve[4] = p[4];
            _suppressEvents = false;
            for (int i = 0; i < 5; i++)
                _device.WriteFloat($"handbrake-y{i + 1}", p[i]);
            _plugin.SaveSettings();
        }

        private void HbCurvePreset_Linear(object s, RoutedEventArgs e) => ApplyHbCurvePreset(HbCurvePresets[0]);
        private void HbCurvePreset_SCurve(object s, RoutedEventArgs e) => ApplyHbCurvePreset(HbCurvePresets[1]);
        private void HbCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyHbCurvePreset(HbCurvePresets[2]);
        private void HbCurvePreset_Parabolic(object s, RoutedEventArgs e) => ApplyHbCurvePreset(HbCurvePresets[3]);

        private void HandbrakeMinSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); if (v > _data.HandbrakeMax) { v = _data.HandbrakeMax; _suppressEvents = true; HandbrakeMinSlider.Value = v; _suppressEvents = false; } HandbrakeMinValue.Text = $"{v}%"; _data.HandbrakeMin = v; _device.WriteSetting("handbrake-min", v); _plugin.SaveSettings(); }
        private void HandbrakeMaxSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); if (v < _data.HandbrakeMin) { v = _data.HandbrakeMin; _suppressEvents = true; HandbrakeMaxSlider.Value = v; _suppressEvents = false; } HandbrakeMaxValue.Text = $"{v}%"; _data.HandbrakeMax = v; _device.WriteSetting("handbrake-max", v); _plugin.SaveSettings(); }
        private void HbY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); HbY1Value.Text = $"{v}"; _data.HandbrakeCurve[0] = v; _device.WriteFloat("handbrake-y1", v); _plugin.SaveSettings(); }
        private void HbY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); HbY2Value.Text = $"{v}"; _data.HandbrakeCurve[1] = v; _device.WriteFloat("handbrake-y2", v); _plugin.SaveSettings(); }
        private void HbY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); HbY3Value.Text = $"{v}"; _data.HandbrakeCurve[2] = v; _device.WriteFloat("handbrake-y3", v); _plugin.SaveSettings(); }
        private void HbY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); HbY4Value.Text = $"{v}"; _data.HandbrakeCurve[3] = v; _device.WriteFloat("handbrake-y4", v); _plugin.SaveSettings(); }
        private void HbY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); HbY5Value.Text = $"{v}"; _data.HandbrakeCurve[4] = v; _device.WriteFloat("handbrake-y5", v); _plugin.SaveSettings(); }

        private void HbCalStartButton_Click(object sender, RoutedEventArgs e)
        {
            _device.WriteSetting("handbrake-cal-start", 1);
            HbCalStatus.Text = "Calibrating — pull fully then stop";
        }

        private void HbCalStopButton_Click(object sender, RoutedEventArgs e)
        {
            _device.WriteSetting("handbrake-cal-stop", 1);
            HbCalStatus.Text = "Done";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (s, _) => { HbCalStatus.Text = ""; ((DispatcherTimer)s!).Stop(); };
            timer.Start();
        }

        // ===== Pedals Tab =====

        // Presets: [Y1, Y2, Y3, Y4, Y5]
        private static readonly int[][] PedalCurvePresets =
        {
            new[] { 20, 40,  60,  80, 100 }, // Linear
            new[] {  8, 24,  76,  92, 100 }, // S Curve
            new[] {  6, 14,  28,  54, 100 }, // Exponential
            new[] { 46, 72,  86,  94, 100 }, // Parabolic
        };

        private void RefreshPedalsTab()
        {
            bool detected = _plugin.IsPedalsDetected;
            PedalsTab.Visibility = detected ? Visibility.Visible : Visibility.Collapsed;
            if (!detected) return;

            ThrottleDirCheck.IsChecked = _data.PedalsThrottleDir != 0;
            ThrottleMinSlider.Value = Clamp(_data.PedalsThrottleMin, 0, 100);
            ThrottleMinValue.Text = $"{_data.PedalsThrottleMin}%";
            ThrottleMaxSlider.Value = Clamp(_data.PedalsThrottleMax, 0, 100);
            ThrottleMaxValue.Text = $"{_data.PedalsThrottleMax}%";
            SetSliderRaw(ThrottleY1Slider, ThrottleY1Value, _data.PedalsThrottleCurve[0], 0, 100, "");
            SetSliderRaw(ThrottleY2Slider, ThrottleY2Value, _data.PedalsThrottleCurve[1], 0, 100, "");
            SetSliderRaw(ThrottleY3Slider, ThrottleY3Value, _data.PedalsThrottleCurve[2], 0, 100, "");
            SetSliderRaw(ThrottleY4Slider, ThrottleY4Value, _data.PedalsThrottleCurve[3], 0, 100, "");
            SetSliderRaw(ThrottleY5Slider, ThrottleY5Value, _data.PedalsThrottleCurve[4], 0, 100, "");

            BrakeDirCheck.IsChecked = _data.PedalsBrakeDir != 0;
            BrakeMinSlider.Value = Clamp(_data.PedalsBrakeMin, 0, 100);
            BrakeMinValue.Text = $"{_data.PedalsBrakeMin}%";
            BrakeMaxSlider.Value = Clamp(_data.PedalsBrakeMax, 0, 100);
            BrakeMaxValue.Text = $"{_data.PedalsBrakeMax}%";
            BrakeAngleRatioSlider.Value = Clamp(_data.PedalsBrakeAngleRatio, 0, 100);
            BrakeAngleRatioValue.Text = $"{_data.PedalsBrakeAngleRatio}%";
            SetSliderRaw(BrakeY1Slider, BrakeY1Value, _data.PedalsBrakeCurve[0], 0, 100, "");
            SetSliderRaw(BrakeY2Slider, BrakeY2Value, _data.PedalsBrakeCurve[1], 0, 100, "");
            SetSliderRaw(BrakeY3Slider, BrakeY3Value, _data.PedalsBrakeCurve[2], 0, 100, "");
            SetSliderRaw(BrakeY4Slider, BrakeY4Value, _data.PedalsBrakeCurve[3], 0, 100, "");
            SetSliderRaw(BrakeY5Slider, BrakeY5Value, _data.PedalsBrakeCurve[4], 0, 100, "");

            ClutchDirCheck.IsChecked = _data.PedalsClutchDir != 0;
            ClutchMinSlider.Value = Clamp(_data.PedalsClutchMin, 0, 100);
            ClutchMinValue.Text = $"{_data.PedalsClutchMin}%";
            ClutchMaxSlider.Value = Clamp(_data.PedalsClutchMax, 0, 100);
            ClutchMaxValue.Text = $"{_data.PedalsClutchMax}%";
            SetSliderRaw(ClutchY1Slider, ClutchY1Value, _data.PedalsClutchCurve[0], 0, 100, "");
            SetSliderRaw(ClutchY2Slider, ClutchY2Value, _data.PedalsClutchCurve[1], 0, 100, "");
            SetSliderRaw(ClutchY3Slider, ClutchY3Value, _data.PedalsClutchCurve[2], 0, 100, "");
            SetSliderRaw(ClutchY4Slider, ClutchY4Value, _data.PedalsClutchCurve[3], 0, 100, "");
            SetSliderRaw(ClutchY5Slider, ClutchY5Value, _data.PedalsClutchCurve[4], 0, 100, "");
        }

        private void ApplyPedalCurvePreset(string pedal, int[] curve, int[] dataArray,
            Slider y1, Slider y2, Slider y3, Slider y4, Slider y5,
            TextBlock l1, TextBlock l2, TextBlock l3, TextBlock l4, TextBlock l5)
        {
            _suppressEvents = true;
            y1.Value = curve[0]; l1.Text = $"{curve[0]}"; dataArray[0] = curve[0];
            y2.Value = curve[1]; l2.Text = $"{curve[1]}"; dataArray[1] = curve[1];
            y3.Value = curve[2]; l3.Text = $"{curve[2]}"; dataArray[2] = curve[2];
            y4.Value = curve[3]; l4.Text = $"{curve[3]}"; dataArray[3] = curve[3];
            y5.Value = curve[4]; l5.Text = $"{curve[4]}"; dataArray[4] = curve[4];
            _suppressEvents = false;
            for (int i = 0; i < 5; i++)
                _device.WriteFloat($"pedals-{pedal}-y{i + 1}", curve[i]);
            _plugin.SaveSettings();
        }

        // Throttle presets
        private void ThrottleCurvePreset_Linear(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("throttle", PedalCurvePresets[0], _data.PedalsThrottleCurve, ThrottleY1Slider, ThrottleY2Slider, ThrottleY3Slider, ThrottleY4Slider, ThrottleY5Slider, ThrottleY1Value, ThrottleY2Value, ThrottleY3Value, ThrottleY4Value, ThrottleY5Value);
        private void ThrottleCurvePreset_SCurve(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("throttle", PedalCurvePresets[1], _data.PedalsThrottleCurve, ThrottleY1Slider, ThrottleY2Slider, ThrottleY3Slider, ThrottleY4Slider, ThrottleY5Slider, ThrottleY1Value, ThrottleY2Value, ThrottleY3Value, ThrottleY4Value, ThrottleY5Value);
        private void ThrottleCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("throttle", PedalCurvePresets[2], _data.PedalsThrottleCurve, ThrottleY1Slider, ThrottleY2Slider, ThrottleY3Slider, ThrottleY4Slider, ThrottleY5Slider, ThrottleY1Value, ThrottleY2Value, ThrottleY3Value, ThrottleY4Value, ThrottleY5Value);
        private void ThrottleCurvePreset_Parabolic(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("throttle", PedalCurvePresets[3], _data.PedalsThrottleCurve, ThrottleY1Slider, ThrottleY2Slider, ThrottleY3Slider, ThrottleY4Slider, ThrottleY5Slider, ThrottleY1Value, ThrottleY2Value, ThrottleY3Value, ThrottleY4Value, ThrottleY5Value);

        // Brake presets
        private void BrakeCurvePreset_Linear(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("brake", PedalCurvePresets[0], _data.PedalsBrakeCurve, BrakeY1Slider, BrakeY2Slider, BrakeY3Slider, BrakeY4Slider, BrakeY5Slider, BrakeY1Value, BrakeY2Value, BrakeY3Value, BrakeY4Value, BrakeY5Value);
        private void BrakeCurvePreset_SCurve(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("brake", PedalCurvePresets[1], _data.PedalsBrakeCurve, BrakeY1Slider, BrakeY2Slider, BrakeY3Slider, BrakeY4Slider, BrakeY5Slider, BrakeY1Value, BrakeY2Value, BrakeY3Value, BrakeY4Value, BrakeY5Value);
        private void BrakeCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("brake", PedalCurvePresets[2], _data.PedalsBrakeCurve, BrakeY1Slider, BrakeY2Slider, BrakeY3Slider, BrakeY4Slider, BrakeY5Slider, BrakeY1Value, BrakeY2Value, BrakeY3Value, BrakeY4Value, BrakeY5Value);
        private void BrakeCurvePreset_Parabolic(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("brake", PedalCurvePresets[3], _data.PedalsBrakeCurve, BrakeY1Slider, BrakeY2Slider, BrakeY3Slider, BrakeY4Slider, BrakeY5Slider, BrakeY1Value, BrakeY2Value, BrakeY3Value, BrakeY4Value, BrakeY5Value);

        // Clutch presets
        private void ClutchCurvePreset_Linear(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("clutch", PedalCurvePresets[0], _data.PedalsClutchCurve, ClutchY1Slider, ClutchY2Slider, ClutchY3Slider, ClutchY4Slider, ClutchY5Slider, ClutchY1Value, ClutchY2Value, ClutchY3Value, ClutchY4Value, ClutchY5Value);
        private void ClutchCurvePreset_SCurve(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("clutch", PedalCurvePresets[1], _data.PedalsClutchCurve, ClutchY1Slider, ClutchY2Slider, ClutchY3Slider, ClutchY4Slider, ClutchY5Slider, ClutchY1Value, ClutchY2Value, ClutchY3Value, ClutchY4Value, ClutchY5Value);
        private void ClutchCurvePreset_Exponential(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("clutch", PedalCurvePresets[2], _data.PedalsClutchCurve, ClutchY1Slider, ClutchY2Slider, ClutchY3Slider, ClutchY4Slider, ClutchY5Slider, ClutchY1Value, ClutchY2Value, ClutchY3Value, ClutchY4Value, ClutchY5Value);
        private void ClutchCurvePreset_Parabolic(object s, RoutedEventArgs e) => ApplyPedalCurvePreset("clutch", PedalCurvePresets[3], _data.PedalsClutchCurve, ClutchY1Slider, ClutchY2Slider, ClutchY3Slider, ClutchY4Slider, ClutchY5Slider, ClutchY1Value, ClutchY2Value, ClutchY3Value, ClutchY4Value, ClutchY5Value);

        // Throttle direction + range + curve sliders
        private void ThrottleDirCheck_Click(object sender, RoutedEventArgs e) { if (_suppressEvents) return; int v = ThrottleDirCheck.IsChecked == true ? 1 : 0; _data.PedalsThrottleDir = v; _device.WriteSetting("pedals-throttle-dir", v); _plugin.SaveSettings(); }
        private void ThrottleMinSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); if (v > _data.PedalsThrottleMax) { v = _data.PedalsThrottleMax; _suppressEvents = true; ThrottleMinSlider.Value = v; _suppressEvents = false; } ThrottleMinValue.Text = $"{v}%"; _data.PedalsThrottleMin = v; _device.WriteSetting("pedals-throttle-min", v); _plugin.SaveSettings(); }
        private void ThrottleMaxSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); if (v < _data.PedalsThrottleMin) { v = _data.PedalsThrottleMin; _suppressEvents = true; ThrottleMaxSlider.Value = v; _suppressEvents = false; } ThrottleMaxValue.Text = $"{v}%"; _data.PedalsThrottleMax = v; _device.WriteSetting("pedals-throttle-max", v); _plugin.SaveSettings(); }
        private void ThrottleY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); ThrottleY1Value.Text = $"{v}"; _data.PedalsThrottleCurve[0] = v; _device.WriteFloat("pedals-throttle-y1", v); _plugin.SaveSettings(); }
        private void ThrottleY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); ThrottleY2Value.Text = $"{v}"; _data.PedalsThrottleCurve[1] = v; _device.WriteFloat("pedals-throttle-y2", v); _plugin.SaveSettings(); }
        private void ThrottleY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); ThrottleY3Value.Text = $"{v}"; _data.PedalsThrottleCurve[2] = v; _device.WriteFloat("pedals-throttle-y3", v); _plugin.SaveSettings(); }
        private void ThrottleY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); ThrottleY4Value.Text = $"{v}"; _data.PedalsThrottleCurve[3] = v; _device.WriteFloat("pedals-throttle-y4", v); _plugin.SaveSettings(); }
        private void ThrottleY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); ThrottleY5Value.Text = $"{v}"; _data.PedalsThrottleCurve[4] = v; _device.WriteFloat("pedals-throttle-y5", v); _plugin.SaveSettings(); }

        // Throttle calibration
        private void ThrottleCalStartButton_Click(object sender, RoutedEventArgs e) { _device.WriteSetting("pedals-throttle-cal-start", 1); }
        private void ThrottleCalStopButton_Click(object sender, RoutedEventArgs e) { _device.WriteSetting("pedals-throttle-cal-stop", 1); }

        // Brake direction + range + curve sliders
        private void BrakeDirCheck_Click(object sender, RoutedEventArgs e) { if (_suppressEvents) return; int v = BrakeDirCheck.IsChecked == true ? 1 : 0; _data.PedalsBrakeDir = v; _device.WriteSetting("pedals-brake-dir", v); _plugin.SaveSettings(); }
        private void BrakeAngleRatioSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); BrakeAngleRatioValue.Text = $"{v}%"; _data.PedalsBrakeAngleRatio = v; _device.WriteFloat("pedals-brake-angle-ratio", v); _plugin.SaveSettings(); }
        private void BrakeMinSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); if (v > _data.PedalsBrakeMax) { v = _data.PedalsBrakeMax; _suppressEvents = true; BrakeMinSlider.Value = v; _suppressEvents = false; } BrakeMinValue.Text = $"{v}%"; _data.PedalsBrakeMin = v; _device.WriteSetting("pedals-brake-min", v); _plugin.SaveSettings(); }
        private void BrakeMaxSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); if (v < _data.PedalsBrakeMin) { v = _data.PedalsBrakeMin; _suppressEvents = true; BrakeMaxSlider.Value = v; _suppressEvents = false; } BrakeMaxValue.Text = $"{v}%"; _data.PedalsBrakeMax = v; _device.WriteSetting("pedals-brake-max", v); _plugin.SaveSettings(); }
        private void BrakeY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); BrakeY1Value.Text = $"{v}"; _data.PedalsBrakeCurve[0] = v; _device.WriteFloat("pedals-brake-y1", v); _plugin.SaveSettings(); }
        private void BrakeY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); BrakeY2Value.Text = $"{v}"; _data.PedalsBrakeCurve[1] = v; _device.WriteFloat("pedals-brake-y2", v); _plugin.SaveSettings(); }
        private void BrakeY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); BrakeY3Value.Text = $"{v}"; _data.PedalsBrakeCurve[2] = v; _device.WriteFloat("pedals-brake-y3", v); _plugin.SaveSettings(); }
        private void BrakeY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); BrakeY4Value.Text = $"{v}"; _data.PedalsBrakeCurve[3] = v; _device.WriteFloat("pedals-brake-y4", v); _plugin.SaveSettings(); }
        private void BrakeY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); BrakeY5Value.Text = $"{v}"; _data.PedalsBrakeCurve[4] = v; _device.WriteFloat("pedals-brake-y5", v); _plugin.SaveSettings(); }

        // Brake calibration
        private void BrakeCalStartButton_Click(object sender, RoutedEventArgs e) { _device.WriteSetting("pedals-brake-cal-start", 1); }
        private void BrakeCalStopButton_Click(object sender, RoutedEventArgs e) { _device.WriteSetting("pedals-brake-cal-stop", 1); }

        // Clutch direction + range + curve sliders
        private void ClutchDirCheck_Click(object sender, RoutedEventArgs e) { if (_suppressEvents) return; int v = ClutchDirCheck.IsChecked == true ? 1 : 0; _data.PedalsClutchDir = v; _device.WriteSetting("pedals-clutch-dir", v); _plugin.SaveSettings(); }
        private void ClutchMinSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); if (v > _data.PedalsClutchMax) { v = _data.PedalsClutchMax; _suppressEvents = true; ClutchMinSlider.Value = v; _suppressEvents = false; } ClutchMinValue.Text = $"{v}%"; _data.PedalsClutchMin = v; _device.WriteSetting("pedals-clutch-min", v); _plugin.SaveSettings(); }
        private void ClutchMaxSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); if (v < _data.PedalsClutchMin) { v = _data.PedalsClutchMin; _suppressEvents = true; ClutchMaxSlider.Value = v; _suppressEvents = false; } ClutchMaxValue.Text = $"{v}%"; _data.PedalsClutchMax = v; _device.WriteSetting("pedals-clutch-max", v); _plugin.SaveSettings(); }
        private void ClutchY1Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); ClutchY1Value.Text = $"{v}"; _data.PedalsClutchCurve[0] = v; _device.WriteFloat("pedals-clutch-y1", v); _plugin.SaveSettings(); }
        private void ClutchY2Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); ClutchY2Value.Text = $"{v}"; _data.PedalsClutchCurve[1] = v; _device.WriteFloat("pedals-clutch-y2", v); _plugin.SaveSettings(); }
        private void ClutchY3Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); ClutchY3Value.Text = $"{v}"; _data.PedalsClutchCurve[2] = v; _device.WriteFloat("pedals-clutch-y3", v); _plugin.SaveSettings(); }
        private void ClutchY4Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); ClutchY4Value.Text = $"{v}"; _data.PedalsClutchCurve[3] = v; _device.WriteFloat("pedals-clutch-y4", v); _plugin.SaveSettings(); }
        private void ClutchY5Slider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (_suppressEvents) return; int v = (int)Math.Round(e.NewValue); ClutchY5Value.Text = $"{v}"; _data.PedalsClutchCurve[4] = v; _device.WriteFloat("pedals-clutch-y5", v); _plugin.SaveSettings(); }

        // Clutch calibration
        private void ClutchCalStartButton_Click(object sender, RoutedEventArgs e) { _device.WriteSetting("pedals-clutch-cal-start", 1); }
        private void ClutchCalStopButton_Click(object sender, RoutedEventArgs e) { _device.WriteSetting("pedals-clutch-cal-stop", 1); }

        // ===== Options tab =====

        private void AutoApplyProfileCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.AutoApplyProfileOnLaunch = AutoApplyProfileCheck.IsChecked == true;
            _plugin.SaveSettings();
        }

        private void LimitWheelUpdatesCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.LimitWheelUpdates = LimitWheelUpdatesCheck.IsChecked == true;
            _plugin.SaveSettings();
        }

        private void WheelKeepaliveCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.WheelKeepalive = WheelKeepaliveCheck.IsChecked == true;
            _plugin.SaveSettings();
        }

        private void AlwaysResendBitmaskCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.AlwaysResendBitmask = AlwaysResendBitmaskCheck.IsChecked == true;
            _plugin.SaveSettings();
        }

        private void ClearAllSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "This will permanently delete all plugin settings and profiles.\n\nAre you sure?",
                "Clear All Settings",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            _plugin.ClearSettings();

            _suppressEvents = true;
            try
            {
                AutoApplyProfileCheck.IsChecked = _plugin.Settings.AutoApplyProfileOnLaunch;
                LimitWheelUpdatesCheck.IsChecked = _plugin.Settings.LimitWheelUpdates;
                WheelKeepaliveCheck.IsChecked = _plugin.Settings.WheelKeepalive;
                ConnectionToggle.IsChecked = _plugin.Settings.ConnectionEnabled;
                ProfileListControl.DataContext = null;
                ProfileListControl.DataContext = _plugin.ProfileStore;
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        // ===== Profile system (SimHub native) =====

        private MozaProfileStore ProfileStore => _plugin.ProfileStore;

        private void InitProfilesTab()
        {
            ProfileListControl.DataContext = ProfileStore;
        }

        // ===== Telemetry tab =====

        private bool _telemetryUIInitialized;

        private void InitTelemetryTab()
        {
            if (_telemetryUIInitialized) return;
            _telemetryUIInitialized = true;

            _suppressEvents = true;
            try
            {
                var s = _plugin.Settings;
                UploadDashboardCheck.IsChecked = s.TelemetryUploadDashboard;
                int protoVer = s.TelemetryProtocolVersion;
                ProtocolVersionCombo.SelectedIndex = protoVer == 0 ? 1 : 0;
                FlagByteModeCombo.SelectedIndex = Math.Max(0, Math.Min(2, s.TelemetryFlagByteMode));
                FlagByteModePanel.IsEnabled = protoVer != 0;
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        private void RefreshTelemetryStatus()
        {
            var sender = _plugin.TelemetrySender;
            if (sender == null) return;

            bool enabled = _plugin.Settings.TelemetryEnabled;
            bool testMode = sender.TestMode;

            if (!enabled)
                TelemetryStatusLabel.Text = "Disabled";
            else if (testMode)
                TelemetryStatusLabel.Text = $"Test pattern — {sender.FramesSent} frames sent";
            else
                TelemetryStatusLabel.Text = $"Sending — {sender.FramesSent} frames sent";

            var last = sender.LastFrameSent;
            TelemetryLastFrameLabel.Text = last != null
                ? BitConverter.ToString(last).Replace("-", " ").ToLowerInvariant()
                : "—";

            // Display sub-device info
            if (_plugin.IsDisplayDetected)
                TelemetryDisplayLabel.Text = $"Display: {_plugin.DisplayModelName} (port 0x{sender.FlagByte:X2})";
            else if (_plugin.IsNewWheelDetected)
                TelemetryDisplayLabel.Text = "Display: not detected (no dashboard screen)";
            else
                TelemetryDisplayLabel.Text = "";

            // Wheel channel catalog
            var catalog = sender.WheelChannelCatalog;
            if (catalog != null && catalog.Count > 0)
                TelemetryWheelChannelsLabel.Text = $"Wheel channels ({catalog.Count}): {string.Join(", ", catalog)}";
            else
                TelemetryWheelChannelsLabel.Text = "";

            TelemetryTestStopBtn.IsEnabled = testMode;
            TelemetryTestStartBtn.IsEnabled = !testMode;
        }

        private void TelemetryTestStart_Click(object sender, RoutedEventArgs e)
        {
            var ts = _plugin.TelemetrySender;
            if (ts == null) return;
            ts.TestMode = true;
            if (!_plugin.Settings.TelemetryEnabled)
            {
                _plugin.ApplyTelemetrySettings();
                ts.Start();
            }
            TelemetryTestStartBtn.IsEnabled = false;
            TelemetryTestStopBtn.IsEnabled = true;
        }

        private void TelemetryTestStop_Click(object sender, RoutedEventArgs e)
        {
            var ts = _plugin.TelemetrySender;
            if (ts == null) return;
            ts.TestMode = false;
            if (!_plugin.Settings.TelemetryEnabled)
                ts.Stop();
            TelemetryTestStartBtn.IsEnabled = true;
            TelemetryTestStopBtn.IsEnabled = false;
        }

        private void UploadDashboard_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.TelemetryUploadDashboard = UploadDashboardCheck.IsChecked == true;
            _plugin.SaveSettings();
            _plugin.RestartTelemetry();
        }

        private void ProtocolVersion_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int version = ProtocolVersionCombo.SelectedIndex == 1 ? 0 : 2;
            _plugin.Settings.TelemetryProtocolVersion = version;
            FlagByteModePanel.IsEnabled = version != 0;
            _plugin.SaveSettings();
            _plugin.RestartTelemetry();
        }

        private void FlagByteMode_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            _plugin.Settings.TelemetryFlagByteMode = FlagByteModeCombo.SelectedIndex;
            _plugin.SaveSettings();
            _plugin.RestartTelemetry();
        }

        private void TelemetryExportLog_Click(object sender, RoutedEventArgs e)
        {
            var ts = _plugin.TelemetrySender;
            if (ts == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save Frame Log",
                Filter = "Text File|*.txt|All Files|*.*",
                FileName = $"moza-telemetry-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
            };
            if (dlg.ShowDialog() != true) return;
            ts.Diagnostics.ExportLog(dlg.FileName);
        }
    }
}
