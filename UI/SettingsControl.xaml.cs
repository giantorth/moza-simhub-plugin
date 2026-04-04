using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MozaTelemetryPlugin
{
    public partial class SettingsControl : UserControl
    {
        private readonly MozaTelemetryPlugin _plugin;
        private readonly MozaDeviceManager _device;
        private readonly MozaTelemetryData _data;
        private readonly DispatcherTimer _refreshTimer;
        private bool _suppressEvents;

        // Color swatch references
        private readonly Border[] _wheelRpmColorSwatches = new Border[10];
        private readonly Border[] _wheelFlagColorSwatches = new Border[6];
        private readonly Border[] _wheelButtonColorSwatches = new Border[14];
        private readonly Border[] _dashRpmColorSwatches = new Border[10];
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
        private static readonly int[][] EsRpmPresets = {
            new[] { 5400, 5700, 6000, 6300, 6500, 6700, 6900, 7100, 7300, 7600 }, // Early
            new[] { 6300, 6600, 6800, 7100, 7300, 7300, 7400, 7500, 7700, 7800 }, // Normal
            new[] { 6700, 6900, 7200, 7400, 7600, 7700, 7800, 7800, 8000, 8100 }, // Late
        };

        public SettingsControl(MozaTelemetryPlugin plugin)
        {
            _plugin = plugin;
            _device = plugin.DeviceManager;
            _data = plugin.Data;

            _suppressEvents = true;
            InitializeComponent();
            ConnectionToggle.IsChecked = plugin.ConnectionEnabled;
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
            BuildSwatchRow(WheelFlagColorPanel, _wheelFlagColorSwatches, 6, "wheel-flag-color", _data.WheelFlagColors);
            BuildSwatchRow(WheelButtonColorPanel, _wheelButtonColorSwatches, 14, "wheel-button-color", _data.WheelButtonColors);
            // Dash colors
            BuildSwatchRow(DashRpmColorPanel, _dashRpmColorSwatches, 10, "dash-rpm-color", _data.DashRpmColors);
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

        private void BuildTimingSliders()
        {
            BuildTimingSliderRow(EsTimingsPercentSliders, _esPercentSliders, _esPercentLabels,
                10, 0, 99, 1, "%", EsPercentSlider_ValueChanged);
            BuildTimingSliderRow(EsTimingsRpmSliders, _esRpmSliders, _esRpmLabels,
                10, 2000, 20000, 100, " RPM", EsRpmSlider_ValueChanged);
            BuildTimingSliderRow(DashTimingsPercentSliders, _dashPercentSliders, _dashPercentLabels,
                10, 0, 99, 1, "%", DashPercentSlider_ValueChanged);
            BuildTimingSliderRow(DashTimingsRpmSliders, _dashRpmSliders, _dashRpmLabels,
                10, 2000, 20000, 100, " RPM", DashRpmSlider_ValueChanged);
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

        private void ApplyEsRpmPreset(int[] values)
        {
            _suppressEvents = true;
            for (int i = 0; i < 10; i++)
            {
                _esRpmSliders[i].Value = values[i];
                _esRpmLabels[i].Text = $"{values[i]} RPM";
                _data.WheelRpmValues[i] = values[i];
                _settings.RpmTimingsRpm[i] = values[i];
            }
            _suppressEvents = false;
            for (int i = 0; i < 10; i++)
                _device.WriteSetting($"wheel-rpm-value{i + 1}", values[i]);
            _plugin.SaveSettings();
        }

        private void EsTimingsPreset_Linear(object s, RoutedEventArgs e) => ApplyEsPercentPreset(EsPercentPresets[0]);
        private void EsTimingsPreset_Early(object s, RoutedEventArgs e) => ApplyEsPercentPreset(EsPercentPresets[1]);
        private void EsTimingsPreset_Normal(object s, RoutedEventArgs e) => ApplyEsPercentPreset(EsPercentPresets[2]);
        private void EsTimingsPreset_Late(object s, RoutedEventArgs e) => ApplyEsPercentPreset(EsPercentPresets[3]);

        private void EsRpmPreset_Early(object s, RoutedEventArgs e) => ApplyEsRpmPreset(EsRpmPresets[0]);
        private void EsRpmPreset_Normal(object s, RoutedEventArgs e) => ApplyEsRpmPreset(EsRpmPresets[1]);
        private void EsRpmPreset_Late(object s, RoutedEventArgs e) => ApplyEsRpmPreset(EsRpmPresets[2]);

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

                WheelRpmBrightnessSlider.Value = Clamp(_data.WheelRpmBrightness, 0, 100);
                WheelRpmBrightnessValue.Text = $"{_data.WheelRpmBrightness}";
                WheelButtonsBrightnessSlider.Value = Clamp(_data.WheelButtonsBrightness, 0, 100);
                WheelButtonsBrightnessValue.Text = $"{_data.WheelButtonsBrightness}";
                WheelFlagsBrightnessSlider.Value = Clamp(_data.WheelFlagsBrightness, 0, 100);
                WheelFlagsBrightnessValue.Text = $"{_data.WheelFlagsBrightness}";

                WheelRpmIntervalSlider.Value = Clamp(_data.WheelRpmInterval, 0, 1000);
                WheelRpmIntervalValue.Text = $"{_data.WheelRpmInterval} ms";

                UpdateSwatches(_wheelRpmColorSwatches, _data.WheelRpmColors, 10);
                UpdateSwatches(_wheelFlagColorSwatches, _data.WheelFlagColors, 6);
                UpdateSwatches(_wheelButtonColorSwatches, _data.WheelButtonColors, 14);
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
                    _esRpmSliders[i].Value = Clamp(_data.WheelRpmValues[i], 2000, 20000);
                    _esRpmLabels[i].Text = $"{_data.WheelRpmValues[i]} RPM";
                }

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
                _dashRpmSliders[i].Value = Clamp(_data.DashRpmValues[i], 2000, 20000);
                _dashRpmLabels[i].Text = $"{_data.DashRpmValues[i]} RPM";
            }

            DashRpmBrightnessSlider.Value = Clamp(_data.DashRpmBrightness, 0, 15);
            DashRpmBrightnessValue.Text = $"{_data.DashRpmBrightness}";
            DashFlagsBrightnessSlider.Value = Clamp(_data.DashFlagsBrightness, 0, 15);
            DashFlagsBrightnessValue.Text = $"{_data.DashFlagsBrightness}";

            DashRpmIntervalSlider.Value = Clamp(_data.DashRpmInterval, 0, 1000);
            DashRpmIntervalValue.Text = $"{_data.DashRpmInterval} ms";

            UpdateSwatches(_dashRpmColorSwatches, _data.DashRpmColors, 10);
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

        private void ApplyDashRpmPreset(int[] values)
        {
            _suppressEvents = true;
            for (int i = 0; i < 10; i++)
            {
                _dashRpmSliders[i].Value = values[i];
                _dashRpmLabels[i].Text = $"{values[i]} RPM";
                _data.DashRpmValues[i] = values[i];
                _settings.DashRpmTimingsRpm[i] = values[i];
            }
            _suppressEvents = false;
            for (int i = 0; i < 10; i++)
                _device.WriteSetting($"dash-rpm-value{i + 1}", values[i]);
            _plugin.SaveSettings();
        }

        private void DashTimingsPreset_Linear(object s, RoutedEventArgs e) => ApplyDashPercentPreset(EsPercentPresets[0]);
        private void DashTimingsPreset_Early(object s, RoutedEventArgs e) => ApplyDashPercentPreset(EsPercentPresets[1]);
        private void DashTimingsPreset_Normal(object s, RoutedEventArgs e) => ApplyDashPercentPreset(EsPercentPresets[2]);
        private void DashTimingsPreset_Late(object s, RoutedEventArgs e) => ApplyDashPercentPreset(EsPercentPresets[3]);

        private void DashRpmPreset_Early(object s, RoutedEventArgs e) => ApplyDashRpmPreset(EsRpmPresets[0]);
        private void DashRpmPreset_Normal(object s, RoutedEventArgs e) => ApplyDashRpmPreset(EsRpmPresets[1]);
        private void DashRpmPreset_Late(object s, RoutedEventArgs e) => ApplyDashRpmPreset(EsRpmPresets[2]);

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

        // ===== Profile system (SimHub native) =====

        private MozaProfileStore ProfileStore => _plugin.ProfileStore;

        private void InitProfilesTab()
        {
            ProfileListControl.DataContext = ProfileStore;
        }
    }
}
