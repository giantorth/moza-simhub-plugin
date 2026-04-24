using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MozaPlugin.Telemetry;

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

        /// <summary>
        /// The virtual LED driver for the device instance this control belongs to.
        /// When set, connection status is derived from the driver's model-aware IsConnected().
        /// When null (legacy), falls back to global plugin state.
        /// </summary>
        internal MozaLedDeviceManager? LinkedLedDriver { get; set; }

        // Color swatch references
        private readonly Border[] _wheelFlagColorSwatches = new Border[6];
        private readonly Border[] _wheelButtonColorSwatches = new Border[14];
        private readonly CheckBox[] _wheelButtonDefaultTelemetryChecks = new CheckBox[14];
        private readonly FrameworkElement[] _wheelButtonSlotContainers = new FrameworkElement[14];
        private const int WheelRpmSwatchMax = 25;
        private readonly Border[] _wheelRpmColorSwatches = new Border[WheelRpmSwatchMax];
        private readonly TextBlock[] _wheelRpmIndexLabels = new TextBlock[WheelRpmSwatchMax];
        private readonly Border[] _wheelKnobBgSwatches = new Border[MozaData.WheelKnobMax];
        private readonly Border[] _wheelKnobPrimarySwatches = new Border[MozaData.WheelKnobMax];
        private readonly FrameworkElement[] _wheelKnobRowContainers = new FrameworkElement[MozaData.WheelKnobMax];

        // ES wheel indicator: device 1=RPM, 2=Off, 3=On (1-based, -1 applied on read)
        // UI combo: 0="SimHub Mode", 1="Always On", 2="Off"
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
            // Flag LEDs live on the Meter sub-device (RS21 DB); swatch writes route via dash-flag-color*.
            BuildSwatchRow(WheelFlagColorPanel, _wheelFlagColorSwatches, 6, "dash-flag-color", _data.WheelFlagColors);
            BuildButtonSwatchRow();
            BuildRpmSwatches();
            BuildKnobSwatchRows();
            _swatchesBuilt = true;
        }

        private void BuildRpmSwatches()
        {
            if (_data == null) return;
            int count = Math.Min(WheelRpmSwatchMax, _data.WheelRpmColors.Length);
            var indexStyle = TryFindResource("LedIndex") as Style;
            for (int i = 0; i < count; i++)
            {
                var idxLabel = new TextBlock { Text = (i + 1).ToString(), Style = indexStyle };
                WheelRpmIndexPanel.Children.Add(idxLabel);
                _wheelRpmIndexLabels[i] = idxLabel;
            }
            BuildSwatchRow(WheelRpmColorPanel, _wheelRpmColorSwatches, count, "wheel-rpm-color", _data.WheelRpmColors);
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

        private void BuildButtonSwatchRow()
        {
            if (_data == null) return;
            const int count = 14;
            for (int i = 0; i < count; i++)
            {
                var col = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Margin = new Thickness(2, 0, 2, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                };

                var border = new Border
                {
                    Width = 28, Height = 28,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(85, 85, 85)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Cursor = Cursors.Hand,
                    Background = Brushes.Black,
                    Tag = new ColorSwatchInfo
                    {
                        CommandPrefix = "wheel-button-color",
                        Index = i,
                        ColorSource = _data.WheelButtonColors,
                    },
                };
                border.MouseLeftButtonUp += ColorSwatch_Click;
                col.Children.Add(border);
                _wheelButtonColorSwatches[i] = border;

                var cb = new CheckBox
                {
                    Margin = new Thickness(0, 4, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    IsChecked = _data.WheelButtonDefaultDuringTelemetry[i],
                    ToolTip = "Default during telemetry: replace 'off' with this button's color whenever SimHub is sending telemetry.",
                    Tag = i,
                };
                cb.Checked += ButtonDefaultTelemetryCheck_Changed;
                cb.Unchecked += ButtonDefaultTelemetryCheck_Changed;
                col.Children.Add(cb);
                _wheelButtonDefaultTelemetryChecks[i] = cb;

                WheelButtonColorPanel.Children.Add(col);
                _wheelButtonSlotContainers[i] = col;
            }
        }

        private void ButtonDefaultTelemetryCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null || _data == null) return;
            var cb = (CheckBox)sender;
            int i = (int)cb.Tag;
            if (i < 0 || i >= _data.WheelButtonDefaultDuringTelemetry.Length) return;
            _data.WheelButtonDefaultDuringTelemetry[i] = cb.IsChecked == true;
            _plugin.SaveSettings();
        }

        private class ColorSwatchInfo
        {
            public string CommandPrefix = "";
            public int Index;
            public byte[][] ColorSource = Array.Empty<byte[]>();
            // When non-empty, used verbatim as the wheel command name instead of
            // "{CommandPrefix}{Index+1}". Used for knob colors whose commands follow
            // the pattern "wheel-knob{N}-bg-color" / "wheel-knob{N}-primary-color".
            public string CommandNameOverride = "";
            // Optional callback fired after a successful picker commit — lets the
            // caller repack the colour into a packed int[] on MozaPluginSettings
            // (knob colours are write-only on the wire, so settings is the only
            // persisted copy).
            public Action? OnChanged;
        }

        private void BuildKnobSwatchRows()
        {
            if (_data == null) return;
            int count = MozaData.WheelKnobMax;
            for (int i = 0; i < count; i++)
            {
                int idx = i;
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 0, 0, 4),
                };
                row.Children.Add(new TextBlock
                {
                    Text = $"Knob {idx + 1}",
                    Width = 70,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var bg = CreateKnobSwatch($"wheel-knob{idx + 1}-bg-color", idx, _data.WheelKnobBackgroundColors, isBackground: true);
                var primary = CreateKnobSwatch($"wheel-knob{idx + 1}-primary-color", idx, _data.WheelKnobPrimaryColors, isBackground: false);
                row.Children.Add(WrapInCell(bg));
                row.Children.Add(WrapInCell(primary));
                WheelKnobPanel.Children.Add(row);
                _wheelKnobBgSwatches[idx] = bg;
                _wheelKnobPrimarySwatches[idx] = primary;
                _wheelKnobRowContainers[idx] = row;
            }
        }

        private static FrameworkElement WrapInCell(Border swatch)
        {
            var cell = new Grid { Width = 60, HorizontalAlignment = HorizontalAlignment.Center };
            swatch.HorizontalAlignment = HorizontalAlignment.Center;
            cell.Children.Add(swatch);
            return cell;
        }

        private Border CreateKnobSwatch(string commandName, int idx, byte[][] colorSource, bool isBackground)
        {
            var border = new Border
            {
                Width = 28, Height = 28,
                BorderBrush = new SolidColorBrush(Color.FromRgb(85, 85, 85)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Cursor = Cursors.Hand,
                Background = Brushes.Black,
                Tag = new ColorSwatchInfo
                {
                    CommandNameOverride = commandName,
                    Index = idx,
                    ColorSource = colorSource,
                    OnChanged = () => PersistKnobColor(idx, isBackground),
                },
            };
            border.MouseLeftButtonUp += ColorSwatch_Click;
            return border;
        }

        private void PersistKnobColor(int idx, bool isBackground)
        {
            if (_data == null || _settings == null) return;
            // Write-only on the wire — settings is the canonical store. Repack the
            // full 3-element array each time so null -> default black is preserved.
            if (isBackground)
                _settings.WheelKnobBackgroundColors = MozaProfile.PackColors(_data.WheelKnobBackgroundColors);
            else
                _settings.WheelKnobPrimaryColors    = MozaProfile.PackColors(_data.WheelKnobPrimaryColors);
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
                string cmdName = !string.IsNullOrEmpty(info.CommandNameOverride)
                    ? info.CommandNameOverride
                    : $"{info.CommandPrefix}{info.Index + 1}";
                _device!.WriteColor(cmdName, r, g, b);
                info.ColorSource[info.Index][0] = r;
                info.ColorSource[info.Index][1] = g;
                info.ColorSource[info.Index][2] = b;
                border.Background = new SolidColorBrush(Color.FromRgb(r, g, b));

                info.OnChanged?.Invoke();
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

            InitTelemetryUI();
            RefreshTelemetryStatus();

            // Use the linked LED driver's model-aware connection check when available
            bool wheelConnected = LinkedLedDriver?.IsConnected() ?? false;
            StatusDot.Fill = wheelConnected ? Brushes.LimeGreen : Brushes.Red;
            StatusText.Text = wheelConnected ? "Connected" : "Disconnected";

            bool isOldProtoDevice = LinkedLedDriver?.ExpectedModelPrefix == MozaDeviceConstants.OldProtocolMarker;
            bool oldWheel = wheelConnected && isOldProtoDevice && _plugin!.IsOldWheelDetected;
            bool newWheel = wheelConnected && !isOldProtoDevice && _plugin!.IsNewWheelDetected;

            if (wheelConnected)
            {
                string modelName = _data!.WheelModelName;
                string swVersion = _data.WheelSwVersion;
                string hwVersion = _data.WheelHwVersion;

                WheelTypeText.Text = string.IsNullOrEmpty(modelName) ? "Detecting wheel..." : modelName;
                var fwParts = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrEmpty(swVersion)) fwParts.Add($"FW: {swVersion}");
                if (!string.IsNullOrEmpty(hwVersion)) fwParts.Add($"HW: {hwVersion}");
                WheelFwText.Text = string.Join("  |  ", fwParts);
            }
            else
            {
                WheelTypeText.Text = "";
                WheelFwText.Text = "";
            }

            _suppressEvents = true;
            try
            {
                bool anyWheel = newWheel || oldWheel;
                WheelNotDetectedPanel.Visibility = anyWheel ? Visibility.Collapsed : Visibility.Visible;
                NewWheelPanel.Visibility = newWheel ? Visibility.Visible : Visibility.Collapsed;
                EsWheelPanel.Visibility = oldWheel ? Visibility.Visible : Visibility.Collapsed;
                TelemetrySection.Visibility = oldWheel ? Visibility.Collapsed : Visibility.Visible;

                if (newWheel)
                {
                    SetComboSafe(WheelTelemetryModeCombo, _data!.WheelTelemetryMode);
                    SetComboSafe(WheelIdleEffectCombo, _data.WheelTelemetryIdleEffect);
                    SetComboSafe(WheelButtonIdleEffectCombo, _data.WheelButtonsIdleEffect);

                    // Show/hide flag and button LED sections based on wheel model
                    var modelInfo = _plugin!.WheelModelInfo;

                    WheelFlagSection.Visibility = (modelInfo?.HasFlagLeds ?? false)
                        ? Visibility.Visible : Visibility.Collapsed;

                    for (int i = 0; i < 14; i++)
                    {
                        var vis = (modelInfo?.IsButtonActive(i) ?? true) ? Visibility.Visible : Visibility.Collapsed;
                        if (_wheelButtonSlotContainers[i] != null)
                            _wheelButtonSlotContainers[i].Visibility = vis;
                        else if (_wheelButtonColorSwatches[i] != null)
                            _wheelButtonColorSwatches[i].Visibility = vis;
                    }

                    int rpmCount = modelInfo?.RpmLedCount ?? 10;
                    for (int i = 0; i < WheelRpmSwatchMax; i++)
                    {
                        var vis = i < rpmCount ? Visibility.Visible : Visibility.Collapsed;
                        if (_wheelRpmColorSwatches[i] != null) _wheelRpmColorSwatches[i].Visibility = vis;
                        if (_wheelRpmIndexLabels[i] != null) _wheelRpmIndexLabels[i].Visibility = vis;
                    }

                    UpdateSwatches(_wheelFlagColorSwatches, _data.WheelFlagColors, 6);
                    UpdateSwatches(_wheelButtonColorSwatches, _data.WheelButtonColors, 14);
                    for (int i = 0; i < 14; i++)
                    {
                        var cb = _wheelButtonDefaultTelemetryChecks[i];
                        if (cb == null) continue;
                        bool want = _data.WheelButtonDefaultDuringTelemetry[i];
                        if ((cb.IsChecked == true) != want) cb.IsChecked = want;
                    }
                    UpdateSwatches(_wheelRpmColorSwatches, _data.WheelRpmColors, rpmCount);

                    int knobCount = modelInfo?.KnobCount ?? 0;
                    WheelKnobSection.Visibility = knobCount > 0 ? Visibility.Visible : Visibility.Collapsed;
                    for (int i = 0; i < MozaData.WheelKnobMax; i++)
                    {
                        var vis = i < knobCount ? Visibility.Visible : Visibility.Collapsed;
                        if (_wheelKnobRowContainers[i] != null)
                            _wheelKnobRowContainers[i].Visibility = vis;
                    }
                    UpdateSwatches(_wheelKnobBgSwatches, _data.WheelKnobBackgroundColors, knobCount);
                    UpdateSwatches(_wheelKnobPrimarySwatches, _data.WheelKnobPrimaryColors, knobCount);
                }

                if (oldWheel)
                {
                    int storedIndicator = _data!.WheelRpmIndicatorMode;
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

        // ===== Dashboard Telemetry =====

        private bool _telemetryUIInitialized;

        private void InitTelemetryUI()
        {
            if (_telemetryUIInitialized || _plugin == null) return;
            _telemetryUIInitialized = true;

            _suppressEvents = true;
            try
            {
                var s = _plugin.Settings;
                TelemetryEnabledCheck.IsChecked = s.TelemetryEnabled;

                TelemetryProfileCombo.Items.Clear();
                foreach (var profile in _plugin.DashProfileStore.BuiltinProfiles)
                    TelemetryProfileCombo.Items.Add(profile.Name);
                if (!string.IsNullOrEmpty(s.TelemetryMzdashPath))
                    TelemetryProfileCombo.Items.Add("[Custom: " + System.IO.Path.GetFileName(s.TelemetryMzdashPath) + "]");

                string selectedName = s.TelemetryProfileName;
                if (!string.IsNullOrEmpty(selectedName))
                {
                    for (int i = 0; i < TelemetryProfileCombo.Items.Count; i++)
                    {
                        if (TelemetryProfileCombo.Items[i]?.ToString() == selectedName)
                        {
                            TelemetryProfileCombo.SelectedIndex = i;
                            break;
                        }
                    }
                }
                if (TelemetryProfileCombo.SelectedIndex < 0 && TelemetryProfileCombo.Items.Count > 0)
                    TelemetryProfileCombo.SelectedIndex = 0;

                UpdateTelemetryProfileInfo();
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        private void RefreshTelemetryStatus()
        {
            if (_plugin == null) return;
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

            TelemetryTestStopBtn.IsEnabled = testMode;
            TelemetryTestStartBtn.IsEnabled = !testMode;
        }

        private void UpdateTelemetryProfileInfo()
        {
            if (_plugin == null) return;
            var profile = _plugin.TelemetrySender?.Profile;
            if (profile == null || profile.Tiers.Count == 0)
            {
                TelemetryProfileInfo.Text = "—";
                return;
            }
            var parts = new System.Collections.Generic.List<string>();
            foreach (var tier in profile.Tiers)
                parts.Add($"L{tier.PackageLevel}: {tier.Channels.Count}ch/{tier.TotalBytes}B");
            TelemetryProfileInfo.Text = string.Join("  ", parts);
        }

        private void TelemetryEnabledCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            _plugin.SetTelemetryEnabled(TelemetryEnabledCheck.IsChecked == true);
            UpdateTelemetryProfileInfo();
        }

        private void TelemetryProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _plugin == null) return;
            var selected = TelemetryProfileCombo.SelectedItem?.ToString();
            if (selected != null && !selected.StartsWith("[Custom:"))
            {
                _plugin.Settings.TelemetryProfileName = selected;
                _plugin.Settings.TelemetryMzdashPath = "";
                _plugin.SaveSettings();
                // Restart so the wheel receives the new tier def + mzdash upload.
                // Without a restart, the wheel keeps the old tier def and decodes
                // the new frame layout as garbage.
                _plugin.RestartTelemetry();
                UpdateTelemetryProfileInfo();
                if (TelemetryMappingsExpander.IsExpanded) PopulateChannelMappingGrid();
            }
        }

        private void TelemetryLoadMzdash_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Open .mzdash dashboard file",
                Filter = "MOZA Dashboard|*.mzdash|All Files|*.*",
                DefaultExt = ".mzdash"
            };
            if (dlg.ShowDialog() != true) return;

            _plugin.Settings.TelemetryMzdashPath = dlg.FileName;
            _plugin.Settings.TelemetryProfileName = "";
            _plugin.SaveSettings();
            // Restart so the wheel receives the new tier def + mzdash upload.
            _plugin.RestartTelemetry();

            _suppressEvents = true;
            string label = "[Custom: " + System.IO.Path.GetFileName(dlg.FileName) + "]";
            for (int i = TelemetryProfileCombo.Items.Count - 1; i >= 0; i--)
                if (TelemetryProfileCombo.Items[i]?.ToString()?.StartsWith("[Custom:") == true)
                    TelemetryProfileCombo.Items.RemoveAt(i);
            TelemetryProfileCombo.Items.Add(label);
            TelemetryProfileCombo.SelectedIndex = TelemetryProfileCombo.Items.Count - 1;
            _suppressEvents = false;

            UpdateTelemetryProfileInfo();
            if (TelemetryMappingsExpander.IsExpanded) PopulateChannelMappingGrid();
        }

        private void TelemetryTestStart_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var ts = _plugin.TelemetrySender;
            if (ts == null) return;
            ts.TestMode = true;
            if (!_plugin.Settings.TelemetryEnabled)
            {
                _plugin.ApplyTelemetrySettings();
                System.Threading.ThreadPool.QueueUserWorkItem(_ => ts.Start());
            }
            TelemetryTestStartBtn.IsEnabled = false;
            TelemetryTestStopBtn.IsEnabled = true;
        }

        private void TelemetryTestStop_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var ts = _plugin.TelemetrySender;
            if (ts == null) return;
            ts.TestMode = false;
            if (!_plugin.Settings.TelemetryEnabled)
                ts.Stop();
            TelemetryTestStartBtn.IsEnabled = true;
            TelemetryTestStopBtn.IsEnabled = false;
        }

        // ===== Channel mappings =====

        private void TelemetryMappingsExpander_Expanded(object sender, RoutedEventArgs e)
            => PopulateChannelMappingGrid();

        private void TelemetryApplyMappings_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null || TelemetryChannelGrid.ItemsSource is not IEnumerable<ChannelMappingRow> rows)
                return;

            foreach (var row in rows)
                _plugin.SetChannelMapping(row.Url, row.SimHubProperty);

            _plugin.RestartTelemetry();
            PopulateChannelMappingGrid();
            TelemetryMappingStatus.Text = $"Applied at {DateTime.Now:HH:mm:ss}";
        }

        private void TelemetryResetMappings_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            _plugin.ClearCurrentDashboardMappings();
            _plugin.RestartTelemetry();
            PopulateChannelMappingGrid();
            TelemetryMappingStatus.Text = $"Reset to defaults at {DateTime.Now:HH:mm:ss}";
        }

        private void PopulateChannelMappingGrid()
        {
            if (_plugin == null) { TelemetryChannelGrid.ItemsSource = null; return; }
            var profile = _plugin.TelemetrySender?.Profile;
            if (profile == null || profile.Tiers.Count == 0)
            {
                TelemetryChannelGrid.ItemsSource = null;
                TelemetryMappingStatus.Text = "(no dashboard loaded)";
                return;
            }

            var rows = new List<ChannelMappingRow>();
            foreach (var tier in profile.Tiers.OrderBy(t => t.PackageLevel))
            {
                foreach (var ch in tier.Channels.OrderBy(c => c.Url, StringComparer.OrdinalIgnoreCase))
                {
                    // Hide plugin-locked channels (resolved internally, not user-editable).
                    if (DashboardProfileStore.IsInternalChannel(ch.SimHubProperty)) continue;

                    rows.Add(new ChannelMappingRow
                    {
                        Name = ch.Name,
                        Url = ch.Url,
                        PackageLevel = ch.PackageLevel,
                        Compression = ch.Compression,
                        SimHubProperty = ch.SimHubProperty ?? "",
                    });
                }
            }
            TelemetryChannelGrid.ItemsSource = rows;
        }

        private sealed class ChannelMappingRow : INotifyPropertyChanged
        {
            public string Name { get; set; } = "";
            public string Url { get; set; } = "";
            public int PackageLevel { get; set; }
            public string Compression { get; set; } = "";

            private string _simHubProperty = "";
            public string SimHubProperty
            {
                get => _simHubProperty;
                set
                {
                    if (_simHubProperty == value) return;
                    _simHubProperty = value ?? "";
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SimHubProperty)));
                }
            }

            public IReadOnlyList<string> KnownProperties => KnownSimHubProperties.Paths;

            public event PropertyChangedEventHandler? PropertyChanged;
        }
    }
}
