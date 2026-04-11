using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MozaPlugin.Devices
{
    public partial class MozaWheelSettingsControl : UserControl
    {
        private MozaPlugin? _plugin;
        private MozaDeviceManager? _device;
        private MozaData? _data;
        private MozaPluginSettings? _settings;
        private bool _suppressEvents;
        private bool _swatchesBuilt;

        private readonly DispatcherTimer _refreshTimer;

        // Color swatch references
        private readonly Border[] _wheelFlagColorSwatches = new Border[6];
        private readonly Border[] _wheelButtonColorSwatches = new Border[14];

        // ES indicator combo reorder: stored 0=SimHub, 1=Off, 2=On
        // Display order: 0=SimHub Mode, 1=Always On, 2=Off
        private static readonly int[] EsIndicatorToDisplay = { 0, 2, 1 };
        private static readonly int[] EsIndicatorToStored = { 0, 2, 1 };

        public MozaWheelSettingsControl()
        {
            _suppressEvents = true;
            InitializeComponent();

            if (ResolvePlugin())
                BuildColorSwatches();

            _suppressEvents = false;

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _refreshTimer.Tick += (s, e) => RefreshWheel();

            Loaded += (s, e) => _refreshTimer.Start();
            Unloaded += (s, e) => _refreshTimer.Stop();
        }

        private bool ResolvePlugin()
        {
            _plugin = MozaPlugin.Instance;
            if (_plugin == null) return false;
            _device = _plugin.DeviceManager;
            _data = _plugin.Data;
            _settings = _plugin.Settings;
            return true;
        }

        // ===== Color swatches =====

        private void BuildColorSwatches()
        {
            if (_swatchesBuilt || _data == null) return;
            BuildSwatchRow(WheelFlagColorPanel, _wheelFlagColorSwatches, 6, "wheel-flag-color", _data.WheelFlagColors);
            BuildSwatchRow(WheelButtonColorPanel, _wheelButtonColorSwatches, 14, "wheel-button-color", _data.WheelButtonColors);
            _swatchesBuilt = true;
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

        private class ColorSwatchInfo
        {
            public string CommandPrefix = "";
            public int Index;
            public byte[][] ColorSource = Array.Empty<byte[]>();
        }

        private void ColorSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            var border = (Border)sender;
            var info = (ColorSwatchInfo)border.Tag;
            var current = info.ColorSource[info.Index];

            var dialog = new ColorPickerDialog(current[0], current[1], current[2]);
            dialog.Owner = Window.GetWindow(this);
            if (dialog.ShowDialog() == true)
            {
                byte r = dialog.SelectedR, g = dialog.SelectedG, b = dialog.SelectedB;
                string cmdName = $"{info.CommandPrefix}{info.Index + 1}";
                _device!.WriteColor(cmdName, r, g, b);
                info.ColorSource[info.Index][0] = r;
                info.ColorSource[info.Index][1] = g;
                info.ColorSource[info.Index][2] = b;
                border.Background = new SolidColorBrush(Color.FromRgb(r, g, b));

                _plugin.SaveSettings();
            }
        }

        // ===== Refresh =====

        private void RefreshWheel()
        {
            if (!ResolvePlugin())
            {
                StatusDot.Fill = Brushes.Gray;
                StatusText.Text = "Plugin not loaded";
                WheelTypeText.Text = "";
                WheelFwText.Text = "";
                WheelNotDetectedPanel.Visibility = Visibility.Visible;
                NewWheelPanel.Visibility = Visibility.Collapsed;
                EsWheelPanel.Visibility = Visibility.Collapsed;
                return;
            }

            if (!_swatchesBuilt)
                BuildColorSwatches();

            // Status
            bool connected = _data!.IsBaseConnected;
            StatusDot.Fill = connected ? Brushes.LimeGreen : Brushes.Red;
            StatusText.Text = connected ? "Connected" : "Disconnected";

            bool newWheel = _plugin!.IsNewWheelDetected;
            bool oldWheel = _plugin.IsOldWheelDetected;

            string modelName = _data!.WheelModelName;
            string swVersion = _data.WheelSwVersion;
            string hwVersion = _data.WheelHwVersion;

            if (newWheel || oldWheel)
            {
                WheelTypeText.Text = string.IsNullOrEmpty(modelName) ? "Detecting wheel..." : modelName;
                var fwParts = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrEmpty(swVersion)) fwParts.Add($"FW: {swVersion}");
                if (!string.IsNullOrEmpty(hwVersion)) fwParts.Add($"HW: {hwVersion}");
                WheelFwText.Text = string.Join("  |  ", fwParts);
            }
            else
            {
                WheelTypeText.Text = connected ? "Detecting..." : "";
                WheelFwText.Text = "";
            }

            _suppressEvents = true;
            try
            {
                bool anyWheel = newWheel || oldWheel;
                WheelNotDetectedPanel.Visibility = anyWheel ? Visibility.Collapsed : Visibility.Visible;
                NewWheelPanel.Visibility = newWheel ? Visibility.Visible : Visibility.Collapsed;
                EsWheelPanel.Visibility = oldWheel ? Visibility.Visible : Visibility.Collapsed;

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
                    StickModeCheck.IsChecked = _data.WheelStickMode != 0;

                    // Show/hide flag and button LED sections based on wheel model
                    var modelInfo = _plugin!.WheelModelInfo;

                    WheelFlagSection.Visibility = (modelInfo?.HasFlagLeds ?? false)
                        ? Visibility.Visible : Visibility.Collapsed;

                    for (int i = 0; i < 14; i++)
                    {
                        if (_wheelButtonColorSwatches[i] != null)
                            _wheelButtonColorSwatches[i].Visibility = (modelInfo?.IsButtonActive(i) ?? true)
                                ? Visibility.Visible : Visibility.Collapsed;
                    }

                    WheelFlagsBrightnessSlider.Value = Clamp(_data.WheelFlagsBrightness, 0, 100);
                    WheelFlagsBrightnessValue.Text = $"{_data.WheelFlagsBrightness}";

                    UpdateSwatches(_wheelFlagColorSwatches, _data.WheelFlagColors, 6);
                    UpdateSwatches(_wheelButtonColorSwatches, _data.WheelButtonColors, 14);
                }

                if (oldWheel)
                {
                    int storedIndicator = _data.WheelRpmIndicatorMode;
                    if (storedIndicator >= 0 && storedIndicator < EsIndicatorToDisplay.Length)
                        SetComboSafe(EsRpmIndicatorCombo, EsIndicatorToDisplay[storedIndicator]);
                    SetComboSafe(EsRpmDisplayCombo, _data.WheelRpmDisplayMode);
                }
            }
            finally
            {
                _suppressEvents = false;
            }
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

        private static void SetComboSafe(ComboBox combo, int index)
        {
            if (index >= 0 && index < combo.Items.Count)
                combo.SelectedIndex = index;
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        // ===== New wheel handlers =====

        private void WheelTelemetryModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = WheelTelemetryModeCombo.SelectedIndex;
            _data!.WheelTelemetryMode = val;
            _settings!.WheelTelemetryMode = val;
            _device!.WriteSetting("wheel-telemetry-mode", val);
            _plugin.SaveSettings();
        }

        private void WheelIdleEffectCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = WheelIdleEffectCombo.SelectedIndex;
            _data!.WheelTelemetryIdleEffect = val;
            _settings!.WheelIdleEffect = val;
            _device!.WriteSetting("wheel-telemetry-idle-effect", val);
            _plugin.SaveSettings();
        }

        private void WheelButtonIdleEffectCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = WheelButtonIdleEffectCombo.SelectedIndex;
            _data!.WheelButtonsIdleEffect = val;
            _settings!.WheelButtonsIdleEffect = val;
            _device!.WriteSetting("wheel-buttons-idle-effect", val);
            _plugin.SaveSettings();
        }

        private void WheelFlagsBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = (int)Math.Round(e.NewValue);
            WheelFlagsBrightnessValue.Text = $"{val}";
            _data!.WheelFlagsBrightness = val;
            _settings!.WheelFlagsBrightness = val;
            _device!.WriteSetting("wheel-flags-brightness", val);
            _plugin.SaveSettings();
        }

        // ===== ES Wheel handlers =====

        private void EsRpmIndicatorCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int display = EsRpmIndicatorCombo.SelectedIndex;
            if (display < 0 || display >= EsIndicatorToStored.Length) return;
            int stored = EsIndicatorToStored[display];
            // ES wheel uses +1 expression: stored 0 -> raw 1, stored 1 -> raw 2, etc.
            int raw = stored + 1;
            _data!.WheelRpmIndicatorMode = stored;
            _settings!.WheelRpmIndicatorMode = stored;
            _device!.WriteSetting("wheel-rpm-indicator-mode", raw);
            _plugin.SaveSettings();
        }

        private void EsRpmDisplayCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = EsRpmDisplayCombo.SelectedIndex;
            _data!.WheelRpmDisplayMode = val;
            _settings!.WheelRpmDisplayMode = val;
            _device!.WriteSetting("wheel-set-rpm-display-mode", val);
            _plugin.SaveSettings();
        }

        // ===== Paddle settings handlers =====

        private void PaddlesModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = PaddlesModeCombo.SelectedIndex;
            _data!.WheelPaddlesMode = val;
            ClutchPointPanel.Visibility = val == 2 ? Visibility.Visible : Visibility.Collapsed;
            _device!.WriteSetting("wheel-paddles-mode", val);
            _plugin.SaveSettings();
        }

        private void ClutchPointSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = (int)Math.Round(e.NewValue);
            ClutchPointValue.Text = $"{val}%";
            _data!.WheelClutchPoint = val;
            _device!.WriteSetting("wheel-clutch-point", val);
            _plugin.SaveSettings();
        }

        private void KnobModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = KnobModeCombo.SelectedIndex;
            _data!.WheelKnobMode = val;
            _device!.WriteSetting("wheel-knob-mode", val);
            _plugin.SaveSettings();
        }

        private void StickModeCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = StickModeCheck.IsChecked == true ? 1 : 0;
            _data!.WheelStickMode = val;
            _device!.WriteSetting("wheel-stick-mode", val);
            _plugin.SaveSettings();
        }
    }
}
