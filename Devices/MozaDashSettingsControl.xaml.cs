using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MozaPlugin.Devices
{
    public partial class MozaDashSettingsControl : UserControl
    {
        private MozaPlugin? _plugin;
        private MozaDeviceManager? _device;
        private MozaData? _data;
        private MozaPluginSettings? _settings;
        private bool _suppressEvents;
        private bool _swatchesBuilt;

        private readonly DispatcherTimer _refreshTimer;

        /// <summary>
        /// The virtual LED driver for the device instance this control belongs to.
        /// When set, connection status is derived from the driver's IsConnected().
        /// </summary>
        internal MozaDashLedDeviceManager? LinkedLedDriver { get; set; }

        // Color swatch references
        private readonly Border[] _dashRpmColorSwatches = new Border[10];
        private readonly Border[] _dashRpmBlinkColorSwatches = new Border[10];
        private readonly Border[] _dashFlagColorSwatches = new Border[6];

        // Device values: 0=Off, 1=RPM (SimHub), 2=On
        // Display order: 0=SimHub Mode, 1=Always On, 2=Off
        private static readonly int[] IndicatorToDisplay = { 2, 0, 1 }; // device 0→Off, 1→SimHub, 2→AlwaysOn
        private static readonly int[] IndicatorToStored = { 1, 2, 0 };  // SimHub→1, AlwaysOn→2, Off→0

        public MozaDashSettingsControl()
        {
            _suppressEvents = true;
            InitializeComponent();

            if (ResolvePlugin())
                BuildColorSwatches();

            _suppressEvents = false;

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _refreshTimer.Tick += OnRefreshTick;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnRefreshTick(object? sender, EventArgs e) => RefreshDash();

        private void OnLoaded(object sender, RoutedEventArgs e) => _refreshTimer.Start();

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _refreshTimer.Stop();
            _refreshTimer.Tick -= OnRefreshTick;
            Loaded -= OnLoaded;
            Unloaded -= OnUnloaded;
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
            BuildSwatchRow(DashRpmColorPanel, _dashRpmColorSwatches, 10, "dash-rpm-color", _data.DashRpmColors);
            BuildBlinkSwatchRow(DashRpmBlinkColorPanel, _dashRpmBlinkColorSwatches, 10, "dash-rpm-blink-color");
            BuildSwatchRow(DashFlagColorPanel, _dashFlagColorSwatches, 6, "dash-flag-color", _data.DashFlagColors);
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

        private void BuildBlinkSwatchRow(StackPanel panel, Border[] swatches, int count,
            string commandPrefix)
        {
            // Blink colors are write-only (can't be polled) — read from saved settings
            var saved = _settings?.DashRpmBlinkColors;
            for (int i = 0; i < count; i++)
            {
                byte r = 0, g = 0, b = 0;
                if (saved != null && i < saved.Length)
                {
                    var rgb = MozaProfile.UnpackColor(saved[i]);
                    r = rgb[0]; g = rgb[1]; b = rgb[2];
                }

                var border = new Border
                {
                    Width = 28, Height = 28,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(85, 85, 85)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(2, 0, 2, 0),
                    Cursor = Cursors.Hand,
                    Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
                    Tag = new BlinkSwatchInfo { CommandPrefix = commandPrefix, Index = i }
                };
                border.MouseLeftButtonUp += BlinkSwatch_Click;
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

        private class BlinkSwatchInfo
        {
            public string CommandPrefix = "";
            public int Index;
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

        private void BlinkSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (_suppressEvents || _plugin == null || _settings == null) return;
            var border = (Border)sender;
            var info = (BlinkSwatchInfo)border.Tag;

            // Read current from saved settings
            byte r = 0, g = 0, b = 0;
            if (_settings.DashRpmBlinkColors != null && info.Index < _settings.DashRpmBlinkColors.Length)
            {
                var rgb = MozaProfile.UnpackColor(_settings.DashRpmBlinkColors[info.Index]);
                r = rgb[0]; g = rgb[1]; b = rgb[2];
            }

            var dialog = new ColorPickerDialog(r, g, b);
            dialog.Owner = Window.GetWindow(this);
            if (dialog.ShowDialog() == true)
            {
                r = dialog.SelectedR; g = dialog.SelectedG; b = dialog.SelectedB;
                string cmdName = $"{info.CommandPrefix}{info.Index + 1}";
                _device!.WriteColor(cmdName, r, g, b);

                // Persist to settings (blink colors are write-only)
                if (_settings.DashRpmBlinkColors == null)
                    _settings.DashRpmBlinkColors = new int[10];
                if (info.Index < _settings.DashRpmBlinkColors.Length)
                    _settings.DashRpmBlinkColors[info.Index] = (r << 16) | (g << 8) | b;

                border.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
                _plugin.SaveSettings();
            }
        }

        // ===== Refresh =====

        private void RefreshDash()
        {
            if (!ResolvePlugin())
            {
                StatusDot.Fill = Brushes.Gray;
                StatusText.Text = "Plugin not loaded";
                DashNotDetectedPanel.Visibility = Visibility.Visible;
                DashPanel.Visibility = Visibility.Collapsed;
                return;
            }

            if (!_swatchesBuilt)
                BuildColorSwatches();

            bool dashConnected = LinkedLedDriver?.IsConnected() ?? false;
            StatusDot.Fill = dashConnected ? Brushes.LimeGreen : Brushes.Red;
            StatusText.Text = dashConnected ? "Connected" : "Disconnected";

            bool dashDetected = dashConnected;

            _suppressEvents = true;
            try
            {
                DashNotDetectedPanel.Visibility = dashDetected ? Visibility.Collapsed : Visibility.Visible;
                DashPanel.Visibility = dashDetected ? Visibility.Visible : Visibility.Collapsed;

                if (dashDetected)
                {
                    int storedRpmIndicator = _data!.DashRpmIndicatorMode;
                    if (storedRpmIndicator >= 0 && storedRpmIndicator < IndicatorToDisplay.Length)
                        SetComboSafe(DashRpmIndicatorCombo, IndicatorToDisplay[storedRpmIndicator]);
                    SetComboSafe(DashRpmDisplayCombo, _data.DashRpmDisplayMode);
                    int storedFlagsIndicator = _data.DashFlagsIndicatorMode;
                    if (storedFlagsIndicator >= 0 && storedFlagsIndicator < IndicatorToDisplay.Length)
                        SetComboSafe(DashFlagsIndicatorCombo, IndicatorToDisplay[storedFlagsIndicator]);

                    DashRpmBrightnessSlider.Value = Clamp(_data.DashRpmBrightness, 0, 100);
                    DashRpmBrightnessValue.Text = $"{_data.DashRpmBrightness}";
                    DashFlagsBrightnessSlider.Value = Clamp(_data.DashFlagsBrightness, 0, 100);
                    DashFlagsBrightnessValue.Text = $"{_data.DashFlagsBrightness}";

                    UpdateSwatches(_dashRpmColorSwatches, _data.DashRpmColors, 10);
                    UpdateSwatches(_dashFlagColorSwatches, _data.DashFlagColors, 6);
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

        // ===== Handlers =====

        private void DashRpmIndicatorCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int display = DashRpmIndicatorCombo.SelectedIndex;
            if (display < 0 || display >= IndicatorToStored.Length) return;
            int stored = IndicatorToStored[display];
            _data!.DashRpmIndicatorMode = stored;
            _device!.WriteSetting("dash-rpm-indicator-mode", stored);
            _plugin.SaveSettings();
        }

        private void DashRpmDisplayCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = DashRpmDisplayCombo.SelectedIndex;
            _data!.DashRpmDisplayMode = val;
            _device!.WriteSetting("dash-rpm-display-mode", val);
            _plugin.SaveSettings();
        }

        private void DashFlagsIndicatorCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            int display = DashFlagsIndicatorCombo.SelectedIndex;
            if (display < 0 || display >= IndicatorToStored.Length) return;
            int stored = IndicatorToStored[display];
            _data!.DashFlagsIndicatorMode = stored;
            _device!.WriteSetting("dash-flags-indicator-mode", stored);
            _plugin.SaveSettings();
        }

        private void DashRpmBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = (int)Math.Round(e.NewValue);
            DashRpmBrightnessValue.Text = $"{val}";
            _data!.DashRpmBrightness = val;
            _settings!.DashRpmBrightness = val;
            _device!.WriteSetting("dash-rpm-brightness", val);
            _plugin.SaveSettings();
        }

        private void DashFlagsBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _plugin == null) return;
            int val = (int)Math.Round(e.NewValue);
            DashFlagsBrightnessValue.Text = $"{val}";
            _data!.DashFlagsBrightness = val;
            _settings!.DashFlagsBrightness = val;
            _device!.WriteSetting("dash-flags-brightness", val);
            _plugin.SaveSettings();
        }
    }
}
