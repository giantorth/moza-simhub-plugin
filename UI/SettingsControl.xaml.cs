using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

        // Color swatch references
        private readonly Border[] _wheelRpmColorSwatches = new Border[10];
        private readonly Border[] _wheelFlagColorSwatches = new Border[6];
        private readonly Border[] _wheelButtonColorSwatches = new Border[14];
        private readonly Border[] _dashRpmColorSwatches = new Border[10];
        private readonly Border[] _wheelBlinkColorSwatches = new Border[10];
        private readonly Border[] _dashRpmBlinkColorSwatches = new Border[10];
        private readonly Border[] _dashFlagColorSwatches = new Border[6];

        // LED test animation
        private DispatcherTimer _testTimer = null!;
        private int _testStep;
        private int _savedIndicatorMode = -1;

        // RPM timing sliders (10 per mode) — wheel
        private readonly Slider[] _esPercentSliders = new Slider[10];
        private readonly TextBlock[] _esPercentLabels = new TextBlock[10];
        private readonly Slider[] _esRpmSliders = new Slider[10];
        private readonly TextBlock[] _esRpmLabels = new TextBlock[10];

        // RPM timing sliders (10 per mode) — dashboard
        private readonly Slider[] _dashPercentSliders = new Slider[10];
        private readonly TextBlock[] _dashPercentLabels = new TextBlock[10];
        private readonly Slider[] _dashRpmSliders = new Slider[10];
        private readonly TextBlock[] _dashRpmLabels = new TextBlock[10];

        // Presets
        private static readonly int[][] EsPercentPresets = {
            new[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 99 }, // Linear
            new[] { 65, 69, 72, 75, 78, 80, 83, 85, 88, 91 }, // Early
            new[] { 75, 79, 82, 85, 87, 88, 89, 90, 92, 94 }, // Normal
            new[] { 80, 83, 86, 89, 91, 92, 93, 94, 96, 97 }, // Late
        };
        // RPM presets as fractions (0.0-1.0) of the current range — same distribution as percent presets
        private static readonly double[][] RpmPresetFractions = {
            new[] { 0.65, 0.69, 0.72, 0.75, 0.78, 0.80, 0.83, 0.85, 0.88, 0.91 }, // Early
            new[] { 0.75, 0.79, 0.82, 0.85, 0.87, 0.88, 0.89, 0.90, 0.92, 0.94 }, // Normal
            new[] { 0.80, 0.83, 0.86, 0.89, 0.91, 0.92, 0.93, 0.94, 0.96, 0.97 }, // Late
        };

        public SettingsControl(MozaPlugin plugin)
        {
            _plugin = plugin;
            _device = plugin.DeviceManager;
            _data = plugin.Data;

            _suppressEvents = true;
            InitializeComponent();
            ConnectionToggle.IsChecked = plugin.ConnectionEnabled;
            AutoApplyProfileCheck.IsChecked = plugin.Settings.AutoApplyProfileOnLaunch;
            _suppressEvents = false;

            BuildColorSwatches();
            BuildTimingSliders();
            InitProfilesTab();

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _refreshTimer.Tick += RefreshDisplay;
            _refreshTimer.Start();

            RequestAllSettings();
        }

        // ===== Color swatches =====

        private void BuildColorSwatches()
        {
            // New wheel colors
            BuildSwatchRow(WheelRpmColorPanel, _wheelRpmColorSwatches, 10, "wheel-rpm-color", _data.WheelRpmColors);
            BuildSwatchRow(WheelBlinkColorPanel, _wheelBlinkColorSwatches, 10, "wheel-rpm-blink-color", _data.WheelRpmBlinkColors);
            BuildSwatchRow(WheelFlagColorPanel, _wheelFlagColorSwatches, 6, "wheel-flag-color", _data.WheelFlagColors);
            BuildSwatchRow(WheelButtonColorPanel, _wheelButtonColorSwatches, 14, "wheel-button-color", _data.WheelButtonColors);
            // Dash colors
            BuildSwatchRow(DashRpmColorPanel, _dashRpmColorSwatches, 10, "dash-rpm-color", _data.DashRpmColors);
            BuildSwatchRow(DashBlinkColorPanel, _dashRpmBlinkColorSwatches, 10, "dash-rpm-blink-color", _data.DashRpmBlinkColors);
            BuildSwatchRow(DashFlagColorPanel, _dashFlagColorSwatches, 6, "dash-flag-color", _data.DashFlagColors);
        }

        private void BuildSwatchRow(StackPanel panel, Border[] swatches, int count,
            string commandPrefix, byte[][] colorSource)
        {
            for (int i = 0; i < count; i++)
            {
                var border = new Border
                {
                    Width = 28, Height = 28,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(85, 85, 85)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(2, 0, 2, 0),
                    Cursor = Cursors.Hand,
                    Background = Brushes.Black,
                    Tag = new ColorSwatchInfo { CommandPrefix = commandPrefix, Index = i, ColorSource = colorSource }
                };
                border.MouseLeftButtonUp += ColorSwatch_Click;
                panel.Children.Add(border);
                swatches[i] = border;
            }
        }

        // ===== Timing sliders =====

        private static int RpmTick(int min, int max) => (max - min) <= 2000 ? 50 : 100;

        private void BuildTimingSliders()
        {
            BuildTimingSliderRow(EsTimingsPercentSliders, _esPercentSliders, _esPercentLabels,
                10, 0, 99, 1, "%", EsPercentSlider_ValueChanged);
            BuildTimingSliderRow(EsTimingsRpmSliders, _esRpmSliders, _esRpmLabels,
                10, _settings.WheelRpmRangeMin, _settings.WheelRpmRangeMax,
                RpmTick(_settings.WheelRpmRangeMin, _settings.WheelRpmRangeMax), " RPM", EsRpmSlider_ValueChanged);
            BuildTimingSliderRow(DashTimingsPercentSliders, _dashPercentSliders, _dashPercentLabels,
                10, 0, 99, 1, "%", DashPercentSlider_ValueChanged);
            BuildTimingSliderRow(DashTimingsRpmSliders, _dashRpmSliders, _dashRpmLabels,
                10, _settings.DashRpmRangeMin, _settings.DashRpmRangeMax,
                RpmTick(_settings.DashRpmRangeMin, _settings.DashRpmRangeMax), " RPM", DashRpmSlider_ValueChanged);
        }

        private void BuildTimingSliderRow(StackPanel panel, Slider[] sliders, TextBlock[] labels,
            int count, int min, int max, int tick, string suffix,
            RoutedPropertyChangedEventHandler<double> handler)
        {
            for (int i = 0; i < count; i++)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };

                var lbl = new TextBlock
                {
                    Text = $"LED {i + 1}",
                    Width = 50, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brushes.Gray, FontSize = 11
                };

                var slider = new Slider
                {
                    Width = 260, Minimum = min, Maximum = max,
                    TickFrequency = tick, IsSnapToTickEnabled = true,
                    VerticalAlignment = VerticalAlignment.Center
                };
                slider.Tag = i;
                slider.ValueChanged += handler;

                var val = new TextBlock
                {
                    Width = 70, TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = new FontFamily("Consolas"),
                    Margin = new Thickness(6, 0, 0, 0),
                    Text = $"{min}{suffix}"
                };

                row.Children.Add(lbl);
                row.Children.Add(slider);
                row.Children.Add(val);
                panel.Children.Add(row);
                sliders[i] = slider;
                labels[i] = val;
            }
        }

        private MozaPluginSettings _settings => _plugin.Settings;

        private void EsPercentSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            var slider = (Slider)sender;
            int idx = (int)slider.Tag;
            int val = (int)Math.Round(e.NewValue);
            _esPercentLabels[idx].Text = $"{val}%";

            var timings = new byte[10];
            for (int i = 0; i < 10; i++)
                timings[i] = (byte)Math.Round(_esPercentSliders[i].Value);
            _data.WheelRpmTimings[idx] = timings[idx];
            _settings.RpmTimingsPercent[idx] = val;
            _device.WriteArray("wheel-rpm-timings", timings);
            _plugin.SaveSettings();
        }

        private void EsRpmSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            var slider = (Slider)sender;
            int idx = (int)slider.Tag;
            int val = (int)Math.Round(e.NewValue);
            _esRpmLabels[idx].Text = $"{val} RPM";
            _data.WheelRpmValues[idx] = val;
            _settings.RpmTimingsRpm[idx] = val;
            _device.WriteSetting($"wheel-rpm-value{idx + 1}", val);
            _plugin.SaveSettings();
        }

        private void ApplyEsPercentPreset(int[] values)
        {
            _suppressEvents = true;
            for (int i = 0; i < 10; i++)
            {
                _esPercentSliders[i].Value = values[i];
                _esPercentLabels[i].Text = $"{values[i]}%";
                _data.WheelRpmTimings[i] = (byte)values[i];
                _settings.RpmTimingsPercent[i] = values[i];
            }
            _suppressEvents = false;
            _device.WriteArray("wheel-rpm-timings", _data.WheelRpmTimings);
            _plugin.SaveSettings();
        }

        private void ApplyEsRpmPreset(double[] fractions)
        {
            int min = _settings.WheelRpmRangeMin, max = _settings.WheelRpmRangeMax;
            _suppressEvents = true;
            for (int i = 0; i < 10; i++)
            {
                int val = min + (int)Math.Round(fractions[i] * (max - min));
                _esRpmSliders[i].Value = val;
                int clamped = (int)_esRpmSliders[i].Value;
                _esRpmLabels[i].Text = $"{clamped} RPM";
                _data.WheelRpmValues[i] = clamped;
                _settings.RpmTimingsRpm[i] = clamped;
            }
            _suppressEvents = false;
            for (int i = 0; i < 10; i++)
                _device.WriteSetting($"wheel-rpm-value{i + 1}", _data.WheelRpmValues[i]);
            _plugin.SaveSettings();
        }

        private void EsTimingsPreset_Linear(object s, RoutedEventArgs e) => ApplyEsPercentPreset(EsPercentPresets[0]);
        private void EsTimingsPreset_Early(object s, RoutedEventArgs e) => ApplyEsPercentPreset(EsPercentPresets[1]);
        private void EsTimingsPreset_Normal(object s, RoutedEventArgs e) => ApplyEsPercentPreset(EsPercentPresets[2]);
        private void EsTimingsPreset_Late(object s, RoutedEventArgs e) => ApplyEsPercentPreset(EsPercentPresets[3]);

        private void EsRpmPreset_Early(object s, RoutedEventArgs e) => ApplyEsRpmPreset(RpmPresetFractions[0]);
        private void EsRpmPreset_Normal(object s, RoutedEventArgs e) => ApplyEsRpmPreset(RpmPresetFractions[1]);
        private void EsRpmPreset_Late(object s, RoutedEventArgs e) => ApplyEsRpmPreset(RpmPresetFractions[2]);

        private class ColorSwatchInfo
        {
            public string CommandPrefix = "";
            public int Index;
            public byte[][] ColorSource = Array.Empty<byte[]>();
        }

        private void ColorSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (_suppressEvents) return;
            var border = (Border)sender;
            var info = (ColorSwatchInfo)border.Tag;
            var current = info.ColorSource[info.Index];

            var dialog = new ColorPickerDialog(current[0], current[1], current[2]);
            dialog.Owner = Window.GetWindow(this);
            if (dialog.ShowDialog() == true)
            {
                byte r = dialog.SelectedR, g = dialog.SelectedG, b = dialog.SelectedB;
                string cmdName = $"{info.CommandPrefix}{info.Index + 1}";
                _device.WriteColor(cmdName, r, g, b);
                info.ColorSource[info.Index][0] = r;
                info.ColorSource[info.Index][1] = g;
                info.ColorSource[info.Index][2] = b;
                border.Background = new SolidColorBrush(Color.FromRgb(r, g, b));

                // Blink colors are write-only (can't be polled) — persist to settings
                if (info.CommandPrefix == "wheel-rpm-blink-color")
                    _plugin.Settings.WheelRpmBlinkColors = MozaProfile.PackColors(_data.WheelRpmBlinkColors);
                else if (info.CommandPrefix == "dash-rpm-blink-color")
                    _plugin.Settings.DashRpmBlinkColors = MozaProfile.PackColors(_data.DashRpmBlinkColors);

                _plugin.SaveSettings();
            }
        }

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
            _suppressEvents = true;
            try
            {
                RefreshBaseTab();
                RefreshWheelTab();
                RefreshDashTab();
                RefreshHandbrakeTab();
                RefreshPedalsTab();
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

        private void RefreshWheelTab()
        {
            // Show/hide panels based on detection
            bool newWheel = _plugin.IsNewWheelDetected;
            bool oldWheel = _plugin.IsOldWheelDetected;

            bool anyWheel = newWheel || oldWheel;
            WheelNotDetectedPanel.Visibility = anyWheel ? Visibility.Collapsed : Visibility.Visible;
            TestLedsPanel.Visibility = anyWheel ? Visibility.Visible : Visibility.Collapsed;
            NewWheelPanel.Visibility = newWheel ? Visibility.Visible : Visibility.Collapsed;
            EsWheelPanel.Visibility = oldWheel ? Visibility.Visible : Visibility.Collapsed;
            WheelTimingsPanel.Visibility = anyWheel ? Visibility.Visible : Visibility.Collapsed;

            if (newWheel)
            {
                SetComboSafe(WheelTelemetryModeCombo, _data.WheelTelemetryMode);
                SetComboSafe(WheelIdleEffectCombo, _data.WheelTelemetryIdleEffect);
                SetComboSafe(WheelButtonIdleEffectCombo, _data.WheelButtonsIdleEffect);
                SetComboSafe(PaddlesModeCombo, _data.WheelPaddlesMode);
                ClutchPointPanel.Visibility = _data.WheelPaddlesMode == 2 ? Visibility.Visible : Visibility.Collapsed;
                ClutchPointSlider.Value = Clamp(_data.WheelClutchPoint, 0, 100);
                ClutchPointValue.Text = $"{_data.WheelClutchPoint}%";
                SetComboSafe(KnobModeCombo, _data.WheelKnobMode);
                StickModeCheck.IsChecked = _data.WheelStickMode != 0; // stick-mode: 0=buttons, non-zero=dpad

                WheelRpmBrightnessSlider.Value = Clamp(_data.WheelRpmBrightness, 0, 100);
                WheelRpmBrightnessValue.Text = $"{_data.WheelRpmBrightness}";
                WheelButtonsBrightnessSlider.Value = Clamp(_data.WheelButtonsBrightness, 0, 100);
                WheelButtonsBrightnessValue.Text = $"{_data.WheelButtonsBrightness}";
                WheelFlagsBrightnessSlider.Value = Clamp(_data.WheelFlagsBrightness, 0, 100);
                WheelFlagsBrightnessValue.Text = $"{_data.WheelFlagsBrightness}";

                WheelRpmIntervalSlider.Value = Clamp(_data.WheelRpmInterval, 0, 1000);
                WheelRpmIntervalValue.Text = $"{_data.WheelRpmInterval} ms";

                UpdateSwatches(_wheelRpmColorSwatches, _data.WheelRpmColors, 10);
                UpdateSwatches(_wheelBlinkColorSwatches, _data.WheelRpmBlinkColors, 10);
                UpdateSwatches(_wheelFlagColorSwatches, _data.WheelFlagColors, 6);
                UpdateSwatches(_wheelButtonColorSwatches, _data.WheelButtonColors, 14);
                SetComboSafe(ButtonTelemetryModeCombo, _plugin.Settings.ButtonTelemetryMode);
            }

            if (oldWheel)
            {
                SetComboSafe(EsRpmIndicatorCombo, _data.WheelRpmIndicatorMode);
                SetComboSafe(EsRpmDisplayCombo, _data.WheelRpmDisplayMode);

                EsRpmBrightnessSlider.Value = Clamp(_data.WheelESRpmBrightness, 0, 15);
                EsRpmBrightnessValue.Text = $"{_data.WheelESRpmBrightness}";
            }

            // Shared timing section (both wheel types)
            if (anyWheel)
            {
                SetComboSafe(EsRpmModeCombo, _data.WheelRpmMode);

                EsTimingsSimHubPanel.Visibility = _data.WheelRpmMode == 2 ? Visibility.Visible : Visibility.Collapsed;
                EsTimingsPercentPanel.Visibility = _data.WheelRpmMode == 0 ? Visibility.Visible : Visibility.Collapsed;
                EsTimingsRpmPanel.Visibility = _data.WheelRpmMode == 1 ? Visibility.Visible : Visibility.Collapsed;

                for (int i = 0; i < 10; i++)
                {
                    _esPercentSliders[i].Value = Clamp(_data.WheelRpmTimings[i], 0, 99);
                    _esPercentLabels[i].Text = $"{_data.WheelRpmTimings[i]}%";
                    _esRpmSliders[i].Value = Clamp(_data.WheelRpmValues[i], _settings.WheelRpmRangeMin, _settings.WheelRpmRangeMax);
                    _esRpmLabels[i].Text = $"{_data.WheelRpmValues[i]} RPM";
                }

                EsRpmRangeMinSlider.Value = Clamp(_settings.WheelRpmRangeMin, 500, 20000);
                EsRpmRangeMinValue.Text = $"{_settings.WheelRpmRangeMin} RPM";
                EsRpmRangeMaxSlider.Value = Clamp(_settings.WheelRpmRangeMax, 500, 20000);
                EsRpmRangeMaxValue.Text = $"{_settings.WheelRpmRangeMax} RPM";

                EsRpmIntervalSlider.Value = Clamp(_data.WheelRpmInterval, 0, 1000);
                EsRpmIntervalValue.Text = $"{_data.WheelRpmInterval} ms";
            }
        }

        private void RefreshDashTab()
        {
            bool detected = _plugin.IsDashDetected;
            DashTab.Visibility = detected ? Visibility.Visible : Visibility.Collapsed;

            if (!detected) return;

            SetComboSafe(DashRpmIndicatorCombo, _data.DashRpmIndicatorMode);
            SetComboSafe(DashRpmDisplayCombo, _data.DashRpmDisplayMode);
            SetComboSafe(DashRpmModeCombo, _data.DashRpmMode);
            SetComboSafe(DashFlagsIndicatorCombo, _data.DashFlagsIndicatorMode);

            DashTimingsSimHubPanel.Visibility = _data.DashRpmMode == 2 ? Visibility.Visible : Visibility.Collapsed;
            DashTimingsPercentPanel.Visibility = _data.DashRpmMode == 0 ? Visibility.Visible : Visibility.Collapsed;
            DashTimingsRpmPanel.Visibility = _data.DashRpmMode == 1 ? Visibility.Visible : Visibility.Collapsed;

            for (int i = 0; i < 10; i++)
            {
                _dashPercentSliders[i].Value = Clamp(_data.DashRpmTimings[i], 0, 99);
                _dashPercentLabels[i].Text = $"{_data.DashRpmTimings[i]}%";
                _dashRpmSliders[i].Value = Clamp(_data.DashRpmValues[i], _settings.DashRpmRangeMin, _settings.DashRpmRangeMax);
                _dashRpmLabels[i].Text = $"{_data.DashRpmValues[i]} RPM";
            }

            DashRpmRangeMinSlider.Value = Clamp(_settings.DashRpmRangeMin, 500, 20000);
            DashRpmRangeMinValue.Text = $"{_settings.DashRpmRangeMin} RPM";
            DashRpmRangeMaxSlider.Value = Clamp(_settings.DashRpmRangeMax, 500, 20000);
            DashRpmRangeMaxValue.Text = $"{_settings.DashRpmRangeMax} RPM";

            DashRpmBrightnessSlider.Value = Clamp(_data.DashRpmBrightness, 0, 15);
            DashRpmBrightnessValue.Text = $"{_data.DashRpmBrightness}";
            DashFlagsBrightnessSlider.Value = Clamp(_data.DashFlagsBrightness, 0, 15);
            DashFlagsBrightnessValue.Text = $"{_data.DashFlagsBrightness}";

            DashRpmIntervalSlider.Value = Clamp(_data.DashRpmInterval, 0, 1000);
            DashRpmIntervalValue.Text = $"{_data.DashRpmInterval} ms";

            UpdateSwatches(_dashRpmColorSwatches, _data.DashRpmColors, 10);
            UpdateSwatches(_dashRpmBlinkColorSwatches, _data.DashRpmBlinkColors, 10);
            UpdateSwatches(_dashFlagColorSwatches, _data.DashFlagColors, 6);
        }

        private static void UpdateSwatches(Border[] swatches, byte[][] colors, int count)
        {
            for (int i = 0; i < count && i < swatches.Length; i++)
            {
                if (swatches[i] == null) continue;
                var c = colors[i];
                swatches[i].Background = new SolidColorBrush(Color.FromRgb(c[0], c[1], c[2]));
            }
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

        // ===== Wheel tab handlers =====

        private void WheelTelemetryModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = WheelTelemetryModeCombo.SelectedIndex;
            _data.WheelTelemetryMode = val;
            _settings.WheelTelemetryMode = val;
            _device.WriteSetting("wheel-telemetry-mode", val);
            _plugin.SaveSettings();
        }

        private void WheelIdleEffectCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = WheelIdleEffectCombo.SelectedIndex;
            _data.WheelTelemetryIdleEffect = val;
            _settings.WheelIdleEffect = val;
            _device.WriteSetting("wheel-telemetry-idle-effect", val);
            _plugin.SaveSettings();
        }

        private void WheelButtonIdleEffectCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = WheelButtonIdleEffectCombo.SelectedIndex;
            _data.WheelButtonsIdleEffect = val;
            _settings.WheelButtonsIdleEffect = val;
            _device.WriteSetting("wheel-buttons-idle-effect", val);
            _plugin.SaveSettings();
        }

        private void ButtonTelemetryModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = ButtonTelemetryModeCombo.SelectedIndex;
            _plugin.Settings.ButtonTelemetryMode = val;
            _plugin.SaveSettings();
        }

        private void WheelRpmBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            WheelRpmBrightnessValue.Text = $"{val}";
            _data.WheelRpmBrightness = val;
            _settings.WheelRpmBrightness = val;
            _device.WriteSetting("wheel-rpm-brightness", val);
            _plugin.SaveSettings();
        }

        private void WheelButtonsBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            WheelButtonsBrightnessValue.Text = $"{val}";
            _data.WheelButtonsBrightness = val;
            _settings.WheelButtonsBrightness = val;
            _device.WriteSetting("wheel-buttons-brightness", val);
            _plugin.SaveSettings();
        }

        private void WheelFlagsBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            WheelFlagsBrightnessValue.Text = $"{val}";
            _data.WheelFlagsBrightness = val;
            _settings.WheelFlagsBrightness = val;
            _device.WriteSetting("wheel-flags-brightness", val);
            _plugin.SaveSettings();
        }

        private void WheelRpmIntervalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            WheelRpmIntervalValue.Text = $"{val} ms";
            _data.WheelRpmInterval = val;
            _device.WriteSetting("wheel-rpm-interval", val);
            _plugin.SaveSettings();
        }

        // ===== ES Wheel handlers =====

        private void EsRpmIndicatorCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int display = EsRpmIndicatorCombo.SelectedIndex;
            // ES wheel uses +1 expression: display 0 -> raw 1, display 1 -> raw 2, etc.
            int raw = display + 1;
            _data.WheelRpmIndicatorMode = display;
            _settings.WheelRpmIndicatorMode = display;
            _device.WriteSetting("wheel-rpm-indicator-mode", raw);
            _plugin.SaveSettings();
        }

        private void EsRpmDisplayCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = EsRpmDisplayCombo.SelectedIndex;
            _data.WheelRpmDisplayMode = val;
            _settings.WheelRpmDisplayMode = val;
            _device.WriteSetting("wheel-set-rpm-display-mode", val);
            _plugin.SaveSettings();
        }

        private void EsRpmModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = EsRpmModeCombo.SelectedIndex;
            _data.WheelRpmMode = val;
            _settings.RpmMode = val;
            EsTimingsSimHubPanel.Visibility = val == 2 ? Visibility.Visible : Visibility.Collapsed;
            EsTimingsPercentPanel.Visibility = val == 0 ? Visibility.Visible : Visibility.Collapsed;
            EsTimingsRpmPanel.Visibility = val == 1 ? Visibility.Visible : Visibility.Collapsed;
            if (val < 2)
                _device.WriteSetting("wheel-rpm-mode", val);
            _plugin.SaveSettings();
        }

        private void EsRpmBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            EsRpmBrightnessValue.Text = $"{val}";
            _data.WheelESRpmBrightness = val;
            _settings.WheelESRpmBrightness = val;
            _device.WriteSetting("wheel-old-rpm-brightness", val);
            _plugin.SaveSettings();
        }

        private void EsRpmIntervalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            EsRpmIntervalValue.Text = $"{val} ms";
            _data.WheelRpmInterval = val;
            _settings.RpmBlinkInterval = val;
            _device.WriteSetting("wheel-rpm-interval", val);
            _plugin.SaveSettings();
        }

        // ===== RPM range slider handlers =====

        private void RebuildRpmSliders(StackPanel panel, Slider[] sliders, TextBlock[] labels,
            int newMin, int newMax, int[] currentValues,
            RoutedPropertyChangedEventHandler<double> handler)
        {
            panel.Children.Clear();
            BuildTimingSliderRow(panel, sliders, labels, 10, newMin, newMax,
                RpmTick(newMin, newMax), " RPM", handler);
            _suppressEvents = true;
            for (int i = 0; i < 10; i++)
            {
                int clamped = (int)Clamp(currentValues[i], newMin, newMax);
                sliders[i].Value = clamped;
                labels[i].Text = $"{clamped} RPM";
            }
            _suppressEvents = false;
        }

        private void EsRpmRangeMinSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            if (val > _settings.WheelRpmRangeMax)
            {
                val = _settings.WheelRpmRangeMax;
                _suppressEvents = true; EsRpmRangeMinSlider.Value = val; _suppressEvents = false;
            }
            EsRpmRangeMinValue.Text = $"{val} RPM";
            _settings.WheelRpmRangeMin = val;
            RebuildRpmSliders(EsTimingsRpmSliders, _esRpmSliders, _esRpmLabels,
                val, _settings.WheelRpmRangeMax, _settings.RpmTimingsRpm, EsRpmSlider_ValueChanged);
            for (int i = 0; i < 10; i++)
            {
                int clamped = (int)_esRpmSliders[i].Value;
                _settings.RpmTimingsRpm[i] = clamped;
                _data.WheelRpmValues[i] = clamped;
                _device.WriteSetting($"wheel-rpm-value{i + 1}", clamped);
            }
            _plugin.SaveSettings();
        }

        private void EsRpmRangeMaxSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            if (val < _settings.WheelRpmRangeMin)
            {
                val = _settings.WheelRpmRangeMin;
                _suppressEvents = true; EsRpmRangeMaxSlider.Value = val; _suppressEvents = false;
            }
            EsRpmRangeMaxValue.Text = $"{val} RPM";
            _settings.WheelRpmRangeMax = val;
            RebuildRpmSliders(EsTimingsRpmSliders, _esRpmSliders, _esRpmLabels,
                _settings.WheelRpmRangeMin, val, _settings.RpmTimingsRpm, EsRpmSlider_ValueChanged);
            for (int i = 0; i < 10; i++)
            {
                int clamped = (int)_esRpmSliders[i].Value;
                _settings.RpmTimingsRpm[i] = clamped;
                _data.WheelRpmValues[i] = clamped;
                _device.WriteSetting($"wheel-rpm-value{i + 1}", clamped);
            }
            _plugin.SaveSettings();
        }

        private void DashRpmRangeMinSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            if (val > _settings.DashRpmRangeMax)
            {
                val = _settings.DashRpmRangeMax;
                _suppressEvents = true; DashRpmRangeMinSlider.Value = val; _suppressEvents = false;
            }
            DashRpmRangeMinValue.Text = $"{val} RPM";
            _settings.DashRpmRangeMin = val;
            RebuildRpmSliders(DashTimingsRpmSliders, _dashRpmSliders, _dashRpmLabels,
                val, _settings.DashRpmRangeMax, _settings.DashRpmTimingsRpm, DashRpmSlider_ValueChanged);
            for (int i = 0; i < 10; i++)
            {
                int clamped = (int)_dashRpmSliders[i].Value;
                _settings.DashRpmTimingsRpm[i] = clamped;
                _data.DashRpmValues[i] = clamped;
                _device.WriteSetting($"dash-rpm-value{i + 1}", clamped);
            }
            _plugin.SaveSettings();
        }

        private void DashRpmRangeMaxSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            if (val < _settings.DashRpmRangeMin)
            {
                val = _settings.DashRpmRangeMin;
                _suppressEvents = true; DashRpmRangeMaxSlider.Value = val; _suppressEvents = false;
            }
            DashRpmRangeMaxValue.Text = $"{val} RPM";
            _settings.DashRpmRangeMax = val;
            RebuildRpmSliders(DashTimingsRpmSliders, _dashRpmSliders, _dashRpmLabels,
                _settings.DashRpmRangeMin, val, _settings.DashRpmTimingsRpm, DashRpmSlider_ValueChanged);
            for (int i = 0; i < 10; i++)
            {
                int clamped = (int)_dashRpmSliders[i].Value;
                _settings.DashRpmTimingsRpm[i] = clamped;
                _data.DashRpmValues[i] = clamped;
                _device.WriteSetting($"dash-rpm-value{i + 1}", clamped);
            }
            _plugin.SaveSettings();
        }

        // ===== Dash tab handlers =====

        private void DashRpmIndicatorCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = DashRpmIndicatorCombo.SelectedIndex;
            _data.DashRpmIndicatorMode = val;
            _device.WriteSetting("dash-rpm-indicator-mode", val);
            _plugin.SaveSettings();
        }

        private void DashRpmDisplayCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = DashRpmDisplayCombo.SelectedIndex;
            _data.DashRpmDisplayMode = val;
            _device.WriteSetting("dash-rpm-display-mode", val);
            _plugin.SaveSettings();
        }

        private void DashRpmModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = DashRpmModeCombo.SelectedIndex;
            _data.DashRpmMode = val;
            _settings.DashRpmMode = val;
            DashTimingsSimHubPanel.Visibility = val == 2 ? Visibility.Visible : Visibility.Collapsed;
            DashTimingsPercentPanel.Visibility = val == 0 ? Visibility.Visible : Visibility.Collapsed;
            DashTimingsRpmPanel.Visibility = val == 1 ? Visibility.Visible : Visibility.Collapsed;
            if (val < 2)
                _device.WriteSetting("dash-rpm-mode", val);
            _plugin.SaveSettings();
        }

        private void DashPercentSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            var slider = (Slider)sender;
            int idx = (int)slider.Tag;
            int val = (int)Math.Round(e.NewValue);
            _dashPercentLabels[idx].Text = $"{val}%";

            var timings = new byte[10];
            for (int i = 0; i < 10; i++)
                timings[i] = (byte)Math.Round(_dashPercentSliders[i].Value);
            _data.DashRpmTimings[idx] = timings[idx];
            _settings.DashRpmTimingsPercent[idx] = val;
            _device.WriteArray("dash-rpm-timings", timings);
            _plugin.SaveSettings();
        }

        private void DashRpmSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            var slider = (Slider)sender;
            int idx = (int)slider.Tag;
            int val = (int)Math.Round(e.NewValue);
            _dashRpmLabels[idx].Text = $"{val} RPM";
            _data.DashRpmValues[idx] = val;
            _settings.DashRpmTimingsRpm[idx] = val;
            _device.WriteSetting($"dash-rpm-value{idx + 1}", val);
            _plugin.SaveSettings();
        }

        private void ApplyDashPercentPreset(int[] values)
        {
            _suppressEvents = true;
            for (int i = 0; i < 10; i++)
            {
                _dashPercentSliders[i].Value = values[i];
                _dashPercentLabels[i].Text = $"{values[i]}%";
                _data.DashRpmTimings[i] = (byte)values[i];
                _settings.DashRpmTimingsPercent[i] = values[i];
            }
            _suppressEvents = false;
            _device.WriteArray("dash-rpm-timings", _data.DashRpmTimings);
            _plugin.SaveSettings();
        }

        private void ApplyDashRpmPreset(double[] fractions)
        {
            int min = _settings.DashRpmRangeMin, max = _settings.DashRpmRangeMax;
            _suppressEvents = true;
            for (int i = 0; i < 10; i++)
            {
                int val = min + (int)Math.Round(fractions[i] * (max - min));
                _dashRpmSliders[i].Value = val;
                int clamped = (int)_dashRpmSliders[i].Value;
                _dashRpmLabels[i].Text = $"{clamped} RPM";
                _data.DashRpmValues[i] = clamped;
                _settings.DashRpmTimingsRpm[i] = clamped;
            }
            _suppressEvents = false;
            for (int i = 0; i < 10; i++)
                _device.WriteSetting($"dash-rpm-value{i + 1}", _data.DashRpmValues[i]);
            _plugin.SaveSettings();
        }

        private void DashTimingsPreset_Linear(object s, RoutedEventArgs e) => ApplyDashPercentPreset(EsPercentPresets[0]);
        private void DashTimingsPreset_Early(object s, RoutedEventArgs e) => ApplyDashPercentPreset(EsPercentPresets[1]);
        private void DashTimingsPreset_Normal(object s, RoutedEventArgs e) => ApplyDashPercentPreset(EsPercentPresets[2]);
        private void DashTimingsPreset_Late(object s, RoutedEventArgs e) => ApplyDashPercentPreset(EsPercentPresets[3]);

        private void DashRpmPreset_Early(object s, RoutedEventArgs e) => ApplyDashRpmPreset(RpmPresetFractions[0]);
        private void DashRpmPreset_Normal(object s, RoutedEventArgs e) => ApplyDashRpmPreset(RpmPresetFractions[1]);
        private void DashRpmPreset_Late(object s, RoutedEventArgs e) => ApplyDashRpmPreset(RpmPresetFractions[2]);

        private void DashFlagsIndicatorCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = DashFlagsIndicatorCombo.SelectedIndex;
            _data.DashFlagsIndicatorMode = val;
            _device.WriteSetting("dash-flags-indicator-mode", val);
            _plugin.SaveSettings();
        }

        private void DashRpmBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            DashRpmBrightnessValue.Text = $"{val}";
            _data.DashRpmBrightness = val;
            _settings.DashRpmBrightness = val;
            _device.WriteSetting("dash-rpm-brightness", val);
            _plugin.SaveSettings();
        }

        private void DashFlagsBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            DashFlagsBrightnessValue.Text = $"{val}";
            _data.DashFlagsBrightness = val;
            _settings.DashFlagsBrightness = val;
            _device.WriteSetting("dash-flags-brightness", val);
            _plugin.SaveSettings();
        }

        private void DashRpmIntervalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            DashRpmIntervalValue.Text = $"{val} ms";
            _data.DashRpmInterval = val;
            _settings.DashRpmBlinkInterval = val;
            _device.WriteSetting("dash-rpm-interval", val);
            _plugin.SaveSettings();
        }

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

        // ===== Test LEDs =====

        // Pattern: light each LED 1-10 individually, then all on, then all off
        private static readonly int[] TestPattern =
        {
            0x001, 0x002, 0x004, 0x008, 0x010,
            0x020, 0x040, 0x080, 0x100, 0x200,
            0x3FF, 0x000
        };

        private void TestLedsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_testTimer != null) return; // already running

            _testStep = 0;
            TestLedsButton.IsEnabled = false;
            TestLedsButton.Content = "Testing...";
            TestLedsStatus.Text = "";

            // Switch wheel into RPM/telemetry mode so bitmask commands are accepted
            if (_plugin.IsOldWheelDetected)
            {
                _savedIndicatorMode = _data.WheelRpmIndicatorMode;
                // ES: raw 1 = RPM mode (display index 0)
                _device.WriteSetting("wheel-rpm-indicator-mode", 1);
            }
            else if (_plugin.IsNewWheelDetected)
            {
                _savedIndicatorMode = _data.WheelTelemetryMode;
                // New wheel: 1 = Telemetry mode
                _device.WriteSetting("wheel-telemetry-mode", 1);
            }

            // Wake up LEDs (ES wheels ignore telemetry until they see a non-zero bitmask)
            _plugin.Sender.SendTestBitmask(0x3FF);
            _plugin.Sender.SendTestBitmask(0x000);

            _testTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _testTimer.Tick += TestLedsTick;
            _testTimer.Start();

            // Send the first step immediately
            _plugin.Sender.SendTestBitmask(TestPattern[0]);
            TestLedsStatus.Text = "LED 1";
            _testStep = 1;
        }

        private void TestLedsTick(object sender, EventArgs e)
        {
            if (_testStep >= TestPattern.Length)
            {
                _testTimer.Stop();
                _testTimer = null!;

                // Restore original indicator mode
                if (_savedIndicatorMode >= 0)
                {
                    if (_plugin.IsOldWheelDetected)
                        _device.WriteSetting("wheel-rpm-indicator-mode", _savedIndicatorMode + 1); // +1: display→raw
                    else if (_plugin.IsNewWheelDetected)
                        _device.WriteSetting("wheel-telemetry-mode", _savedIndicatorMode);
                    _savedIndicatorMode = -1;
                }

                TestLedsButton.IsEnabled = true;
                TestLedsButton.Content = "Test LEDs";
                TestLedsStatus.Text = "";
                return;
            }

            _plugin.Sender.SendTestBitmask(TestPattern[_testStep]);

            if (_testStep < 10)
                TestLedsStatus.Text = $"LED {_testStep + 1}";
            else if (_testStep == 10)
                TestLedsStatus.Text = "All on";
            else
                TestLedsStatus.Text = "";

            _testStep++;
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

        // ===== Wheel Paddle Settings =====

        private void PaddlesModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = PaddlesModeCombo.SelectedIndex;
            _data.WheelPaddlesMode = val;
            ClutchPointPanel.Visibility = val == 2 ? Visibility.Visible : Visibility.Collapsed;
            _device.WriteSetting("wheel-paddles-mode", val);
            _plugin.SaveSettings();
        }

        private void ClutchPointSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents) return;
            int val = (int)Math.Round(e.NewValue);
            ClutchPointValue.Text = $"{val}%";
            _data.WheelClutchPoint = val;
            _device.WriteSetting("wheel-clutch-point", val);
            _plugin.SaveSettings();
        }

        private void KnobModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = KnobModeCombo.SelectedIndex;
            _data.WheelKnobMode = val;
            _device.WriteSetting("wheel-knob-mode", val);
            _plugin.SaveSettings();
        }

        private void StickModeCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            int val = StickModeCheck.IsChecked == true ? 1 : 0;
            _data.WheelStickMode = val;
            _device.WriteSetting("wheel-stick-mode", val);
            _plugin.SaveSettings();
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
    }
}
