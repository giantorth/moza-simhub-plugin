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

        /// <summary>
        /// The virtual LED driver for the device instance this control belongs to.
        /// When set, connection status is derived from the driver's model-aware IsConnected().
        /// When null (legacy), falls back to global plugin state.
        /// </summary>
        internal MozaLedDeviceManager? LinkedLedDriver { get; set; }

        // Color swatch references
        private readonly Border[] _wheelFlagColorSwatches = new Border[6];
        private readonly Border[] _wheelButtonColorSwatches = new Border[14];
        private const int WheelRpmSwatchMax = 25;
        private readonly Border[] _wheelRpmColorSwatches = new Border[WheelRpmSwatchMax];
        private readonly TextBlock[] _wheelRpmIndexLabels = new TextBlock[WheelRpmSwatchMax];

        // Diagnostic LED panel state (keyed by slot: 0=Shift/RPM, 1=Button,
        // 2/3/4=extended groups, 5=Meter flags)
        private readonly bool[] _extLedPanelBuilt = new bool[6];
        private readonly byte[] _extLedFillR = new byte[6];
        private readonly byte[] _extLedFillG = new byte[6];
        private readonly byte[] _extLedFillB = new byte[6];
        private readonly Border[] _extLedSwatches = new Border[6];
        private readonly int[] _extLedRangeMin = new int[6];
        private readonly int[] _extLedRangeMax = new int[6];
        // Per-slot TextBox refs so LostFocus handlers + summary refresh can read current values
        private readonly TextBox?[] _extLedMinBoxes = new TextBox?[6];
        private readonly TextBox?[] _extLedMaxBoxes = new TextBox?[6];

        private class DiagLedCfg
        {
            public int Slot;
            public string Title = "";
            public int MaxLeds;
            public string ColorCmdPrefix = "";  // "wheel-group2-color" → "wheel-group2-color{N}". Ignored when LiveColorCmd set.
            public string BrightnessCmd = "";
            public string? ModeCmd;             // null = skip mode row
            // When both set, writes go through the live telemetry pipeline (bulk chunk + bitmask)
            // instead of per-LED static EEPROM writes. Used for Button group where static writes
            // only render in idle/constant mode; live writes hit the telemetry frame buffer.
            public string? LiveColorCmd;
            public string? LiveBitmaskCmd;
        }

        private static readonly DiagLedCfg[] DiagLedCfgs =
        {
            // Groups 0/1 mode skipped — wheel-telemetry-mode / idle-effect already drive this UI elsewhere.
            new DiagLedCfg { Slot = 0, Title = "Group 0 — Shift/RPM", MaxLeds = 25, ColorCmdPrefix = "wheel-rpm-color",    BrightnessCmd = "wheel-rpm-brightness",     ModeCmd = null,
                             LiveColorCmd = "wheel-telemetry-rpm-colors", LiveBitmaskCmd = "wheel-send-rpm-telemetry" },
            new DiagLedCfg { Slot = 1, Title = "Group 1 — Button",   MaxLeds = 16, ColorCmdPrefix = "wheel-button-color", BrightnessCmd = "wheel-buttons-brightness", ModeCmd = null,
                             LiveColorCmd = "wheel-telemetry-button-colors", LiveBitmaskCmd = "wheel-send-buttons-telemetry" },
            new DiagLedCfg { Slot = 2, Title = "Group 2 — Single",   MaxLeds = 28, ColorCmdPrefix = "wheel-group2-color", BrightnessCmd = "wheel-group2-brightness",  ModeCmd = "wheel-group2-mode" },
            new DiagLedCfg { Slot = 3, Title = "Group 3 — Rotary",   MaxLeds = 56, ColorCmdPrefix = "wheel-group3-color", BrightnessCmd = "wheel-group3-brightness",  ModeCmd = "wheel-group3-mode" },
            new DiagLedCfg { Slot = 4, Title = "Group 4 — Ambient",  MaxLeds = 12, ColorCmdPrefix = "wheel-group4-color", BrightnessCmd = "wheel-group4-brightness",  ModeCmd = "wheel-group4-mode" },
            new DiagLedCfg { Slot = 5, Title = "Flags (Meter device)", MaxLeds = 6, ColorCmdPrefix = "dash-flag-color",   BrightnessCmd = "dash-flags-brightness",    ModeCmd = null },
        };

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
            BuildSwatchRow(WheelButtonColorPanel, _wheelButtonColorSwatches, 14, "wheel-button-color", _data.WheelButtonColors);
            BuildRpmSwatches();
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
                        if (_wheelButtonColorSwatches[i] != null)
                            _wheelButtonColorSwatches[i].Visibility = (modelInfo?.IsButtonActive(i) ?? true)
                                ? Visibility.Visible : Visibility.Collapsed;
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
                    UpdateSwatches(_wheelRpmColorSwatches, _data.WheelRpmColors, rpmCount);

                    RefreshExtendedLedGroups();
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

        // ===== Experimental LED diagnostics (groups 2/3/4 + Meter flags) =====

        private void RefreshExtendedLedGroups()
        {
            if (_plugin == null) return;

            bool any = false;
            bool built = false;
            foreach (var cfg in DiagLedCfgs)
            {
                bool present = IsDiagSlotPresent(cfg.Slot);
                if (present && !_extLedPanelBuilt[cfg.Slot])
                {
                    BuildDiagLedPanel(cfg);
                    _extLedPanelBuilt[cfg.Slot] = true;
                    built = true;
                }
                var panel = GetDiagLedPanel(cfg.Slot);
                if (panel != null)
                    panel.Visibility = present ? Visibility.Visible : Visibility.Collapsed;
                any |= present;
            }
            ExtLedSection.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            if (built)
                RefreshExtendedLedSummary();
        }

        private void RefreshExtendedLedSummary()
        {
            if (_plugin == null) return;

            var sb = new System.Text.StringBuilder();
            var info = _plugin.WheelModelInfo;
            string modelName = _data?.WheelModelName ?? "";
            string friendly = string.IsNullOrEmpty(modelName) ? "Unknown wheel"
                : $"{WheelModelInfo.GetFriendlyName(WheelModelInfo.ExtractPrefix(modelName))} ({modelName})";
            sb.AppendLine($"{friendly} — wheel LED support");
            if (info != null)
                sb.AppendLine($"  WheelModelInfo: rpm={info.RpmLedCount}, buttons={info.ButtonLedCount}, flags={info.HasFlagLeds}");
            sb.AppendLine();

            foreach (var cfg in DiagLedCfgs)
            {
                if (!_extLedPanelBuilt[cfg.Slot]) continue;
                int min = _extLedRangeMin[cfg.Slot];
                int max = _extLedRangeMax[cfg.Slot];
                int count = max - min + 1;
                sb.AppendLine($"  {cfg.Title,-28} : {count,3} LEDs (indices {min}-{max} of 0-{cfg.MaxLeds - 1})");
            }
            ExtLedSummaryBox.Text = sb.ToString();
        }

        private void ExtLedRangeMin_LostFocus(object sender, RoutedEventArgs e) =>
            ExtLedRangeChanged((TextBox)sender, isMax: false);

        private void ExtLedRangeMax_LostFocus(object sender, RoutedEventArgs e) =>
            ExtLedRangeChanged((TextBox)sender, isMax: true);

        private void ExtLedRangeChanged(TextBox box, bool isMax)
        {
            var cfg = (DiagLedCfg)box.Tag;
            if (!int.TryParse(box.Text, out int v))
                v = isMax ? cfg.MaxLeds - 1 : 0;
            v = Math.Max(0, Math.Min(v, cfg.MaxLeds - 1));

            if (isMax)
            {
                if (v < _extLedRangeMin[cfg.Slot]) v = _extLedRangeMin[cfg.Slot];
                _extLedRangeMax[cfg.Slot] = v;
            }
            else
            {
                if (v > _extLedRangeMax[cfg.Slot]) v = _extLedRangeMax[cfg.Slot];
                _extLedRangeMin[cfg.Slot] = v;
            }
            box.Text = v.ToString();

            if (_settings != null)
            {
                _settings.ExtLedDiagMin[cfg.Slot] = _extLedRangeMin[cfg.Slot];
                _settings.ExtLedDiagMax[cfg.Slot] = _extLedRangeMax[cfg.Slot];
                _plugin?.SaveSettings();
            }
            RefreshExtendedLedSummary();
        }

        private bool IsDiagSlotPresent(int slot)
        {
            if (_plugin == null) return false;
            // Slots 0/1: Shift/RPM + Button — universally present on any new-protocol wheel.
            if (slot == 0 || slot == 1) return _plugin.IsNewWheelDetected;
            // Slots 2..4: extended wheel groups detected via brightness-read probe.
            //   NOTE: probe can false-positive — firmware accepts the read even when
            //   no physical hardware is present. Keep surfacing so we can map support per model.
            if (slot >= 2 && slot <= 4) return _plugin.IsWheelLedGroupPresent(slot);
            // Slot 5: Meter flag LEDs — show whenever the Meter sub-device is detected.
            if (slot == 5) return _plugin.IsDashDetected;
            return false;
        }

        private StackPanel? GetDiagLedPanel(int slot) => slot switch
        {
            0 => ExtLedGroup0Panel,
            1 => ExtLedGroup1Panel,
            2 => ExtLedGroup2Panel,
            3 => ExtLedGroup3Panel,
            4 => ExtLedGroup4Panel,
            5 => ExtLedFlagsPanel,
            _ => null,
        };

        private void BuildDiagLedPanel(DiagLedCfg cfg)
        {
            var panel = GetDiagLedPanel(cfg.Slot);
            if (panel == null) return;

            panel.Children.Clear();

            panel.Children.Add(new TextBlock
            {
                Text = $"{cfg.Title} (up to {cfg.MaxLeds} LEDs)",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 4),
            });

            // Load persisted min/max for this slot, defaulting to full range
            int savedMin = _settings != null && _settings.ExtLedDiagMin.Length > cfg.Slot ? _settings.ExtLedDiagMin[cfg.Slot] : -1;
            int savedMax = _settings != null && _settings.ExtLedDiagMax.Length > cfg.Slot ? _settings.ExtLedDiagMax[cfg.Slot] : -1;
            _extLedRangeMin[cfg.Slot] = savedMin < 0 ? 0 : Math.Max(0, Math.Min(savedMin, cfg.MaxLeds - 1));
            _extLedRangeMax[cfg.Slot] = savedMax < 0 ? cfg.MaxLeds - 1 : Math.Max(0, Math.Min(savedMax, cfg.MaxLeds - 1));
            if (_extLedRangeMin[cfg.Slot] > _extLedRangeMax[cfg.Slot])
                _extLedRangeMin[cfg.Slot] = _extLedRangeMax[cfg.Slot];

            // Fill color swatch + Min/Max + Fill-all / Clear buttons
            var row1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row1.Children.Add(new TextBlock { Text = "Fill color:", Width = 80, VerticalAlignment = VerticalAlignment.Center });

            _extLedFillR[cfg.Slot] = 255;
            _extLedFillG[cfg.Slot] = 0;
            _extLedFillB[cfg.Slot] = 0;
            var swatch = new Border
            {
                Width = 28, Height = 28,
                BorderBrush = new SolidColorBrush(Color.FromRgb(85, 85, 85)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Color.FromRgb(255, 0, 0)),
                Tag = cfg.Slot,
            };
            swatch.MouseLeftButtonUp += ExtLedSwatch_Click;
            _extLedSwatches[cfg.Slot] = swatch;
            row1.Children.Add(swatch);

            row1.Children.Add(new TextBlock { Text = "Range:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            var minBox = new TextBox { Width = 40, Text = _extLedRangeMin[cfg.Slot].ToString(), Margin = new Thickness(0, 0, 2, 0), Tag = cfg, VerticalAlignment = VerticalAlignment.Center };
            minBox.LostFocus += ExtLedRangeMin_LostFocus;
            _extLedMinBoxes[cfg.Slot] = minBox;
            row1.Children.Add(minBox);
            row1.Children.Add(new TextBlock { Text = "–", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) });
            var maxBox = new TextBox { Width = 40, Text = _extLedRangeMax[cfg.Slot].ToString(), Margin = new Thickness(0, 0, 8, 0), Tag = cfg, VerticalAlignment = VerticalAlignment.Center };
            maxBox.LostFocus += ExtLedRangeMax_LostFocus;
            _extLedMaxBoxes[cfg.Slot] = maxBox;
            row1.Children.Add(maxBox);

            var fillBtn = new Button { Content = "Fill all", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0), Tag = cfg };
            fillBtn.Click += ExtLedFillAll_Click;
            row1.Children.Add(fillBtn);

            var clearBtn = new Button { Content = "All off", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0), Tag = cfg };
            clearBtn.Click += ExtLedClearAll_Click;
            row1.Children.Add(clearBtn);
            panel.Children.Add(row1);

            // Single-LED write: index + Send
            var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row2.Children.Add(new TextBlock { Text = "LED index:", Width = 80, VerticalAlignment = VerticalAlignment.Center });
            var idxBox = new TextBox { Width = 50, Text = "0", Margin = new Thickness(0, 0, 8, 0) };
            row2.Children.Add(idxBox);
            var sendOneBtn = new Button { Content = "Send one", Padding = new Thickness(8, 2, 8, 2), Tag = (cfg, idxBox) };
            sendOneBtn.Click += ExtLedSendOne_Click;
            row2.Children.Add(sendOneBtn);
            panel.Children.Add(row2);

            // Brightness slider + send
            var row3 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row3.Children.Add(new TextBlock { Text = "Brightness:", Width = 80, VerticalAlignment = VerticalAlignment.Center });
            var brightSlider = new Slider { Minimum = 0, Maximum = 100, Value = 50, Width = 200, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
            row3.Children.Add(brightSlider);
            var brightLabel = new TextBlock { Width = 40, TextAlignment = TextAlignment.Right, Margin = new Thickness(6, 0, 8, 0), Text = "50", VerticalAlignment = VerticalAlignment.Center };
            brightSlider.ValueChanged += (s, e) => brightLabel.Text = ((int)brightSlider.Value).ToString();
            row3.Children.Add(brightLabel);
            var sendBrightBtn = new Button { Content = "Send", Padding = new Thickness(8, 2, 8, 2), Tag = (cfg, brightSlider) };
            sendBrightBtn.Click += ExtLedSendBrightness_Click;
            row3.Children.Add(sendBrightBtn);
            panel.Children.Add(row3);

            // Mode byte (optional) — 0=off, 1=telemetry-active, 2=static (tentative)
            if (cfg.ModeCmd != null)
            {
                var row4 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                row4.Children.Add(new TextBlock { Text = "Mode:", Width = 80, VerticalAlignment = VerticalAlignment.Center });
                var modeCombo = new ComboBox { Width = 60, Margin = new Thickness(0, 0, 8, 0) };
                modeCombo.Items.Add("0");
                modeCombo.Items.Add("1");
                modeCombo.Items.Add("2");
                modeCombo.SelectedIndex = 0;
                row4.Children.Add(modeCombo);
                var sendModeBtn = new Button { Content = "Send", Padding = new Thickness(8, 2, 8, 2), Tag = (cfg, modeCombo) };
                sendModeBtn.Click += ExtLedSendMode_Click;
                row4.Children.Add(sendModeBtn);
                panel.Children.Add(row4);
            }
        }

        private void ExtLedSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (_plugin == null) return;
            var border = (Border)sender;
            int slot = (int)border.Tag;
            var dialog = new ColorPickerDialog(_extLedFillR[slot], _extLedFillG[slot], _extLedFillB[slot]);
            dialog.Owner = Window.GetWindow(this);
            if (dialog.ShowDialog() == true)
            {
                _extLedFillR[slot] = dialog.SelectedR;
                _extLedFillG[slot] = dialog.SelectedG;
                _extLedFillB[slot] = dialog.SelectedB;
                border.Background = new SolidColorBrush(Color.FromRgb(dialog.SelectedR, dialog.SelectedG, dialog.SelectedB));
            }
        }

        private void ExtLedFillAll_Click(object sender, RoutedEventArgs e)
        {
            if (_device == null) return;
            var cfg = (DiagLedCfg)((Button)sender).Tag;
            byte r = _extLedFillR[cfg.Slot], g = _extLedFillG[cfg.Slot], b = _extLedFillB[cfg.Slot];
            int min = _extLedRangeMin[cfg.Slot], max = _extLedRangeMax[cfg.Slot];
            if (cfg.LiveColorCmd != null)
            {
                int mask = RangeMask(min, max);
                SendLiveFrame(cfg, fillColor: (r, g, b), activeMask: mask, rangeMin: min, rangeMax: max, onlyIdx: -1);
                return;
            }
            for (int i = min; i <= max; i++)
                _device.WriteColor($"{cfg.ColorCmdPrefix}{i + 1}", r, g, b);
        }

        private void ExtLedClearAll_Click(object sender, RoutedEventArgs e)
        {
            if (_device == null) return;
            var cfg = (DiagLedCfg)((Button)sender).Tag;
            int min = _extLedRangeMin[cfg.Slot], max = _extLedRangeMax[cfg.Slot];
            if (cfg.LiveColorCmd != null)
            {
                // Live pipeline: bitmask=0 removes live override. Firmware falls back to static or off.
                SendLiveFrame(cfg, fillColor: (0, 0, 0), activeMask: 0, rangeMin: min, rangeMax: max, onlyIdx: -1);
                return;
            }
            for (int i = min; i <= max; i++)
                _device.WriteColor($"{cfg.ColorCmdPrefix}{i + 1}", 0, 0, 0);
        }

        private void ExtLedSendOne_Click(object sender, RoutedEventArgs e)
        {
            if (_device == null) return;
            var (cfg, idxBox) = ((DiagLedCfg, TextBox))((Button)sender).Tag;
            if (!int.TryParse(idxBox.Text, out int idx)) return;
            if (idx < 0 || idx >= cfg.MaxLeds) return;
            byte r = _extLedFillR[cfg.Slot], g = _extLedFillG[cfg.Slot], b = _extLedFillB[cfg.Slot];
            if (cfg.LiveColorCmd != null)
            {
                SendLiveFrame(cfg, fillColor: (r, g, b), activeMask: 1 << idx, rangeMin: idx, rangeMax: idx, onlyIdx: idx);
                return;
            }
            _device.WriteColor($"{cfg.ColorCmdPrefix}{idx + 1}", r, g, b);
        }

        /// <summary>Build a bitmask with bits [min..max] (inclusive) set.</summary>
        private static int RangeMask(int min, int max)
        {
            int mask = 0;
            for (int i = min; i <= max; i++)
                mask |= 1 << i;
            return mask;
        }

        /// <summary>
        /// Send one frame through the live telemetry pipeline: per-LED colors chunked via
        /// <see cref="MozaLedDeviceManager.SendColorChunks"/>, then a 2-byte LE bitmask.
        /// Frame buffer is volatile — firmware overwrites it on the next live update from
        /// the telemetry sender or SimHub effects loop.
        /// </summary>
        private void SendLiveFrame(DiagLedCfg cfg, (byte r, byte g, byte b) fillColor,
            int activeMask, int rangeMin, int rangeMax, int onlyIdx)
        {
            if (_device == null || _plugin == null || cfg.LiveColorCmd == null || cfg.LiveBitmaskCmd == null) return;

            int n = cfg.MaxLeds;
            var colors = new System.Drawing.Color[n];
            var fill = System.Drawing.Color.FromArgb(fillColor.r, fillColor.g, fillColor.b);
            for (int i = 0; i < n; i++)
            {
                bool paint = onlyIdx < 0
                    ? (i >= rangeMin && i <= rangeMax)
                    : i == onlyIdx;
                colors[i] = paint ? fill : System.Drawing.Color.Black;
            }

            MozaLedDeviceManager.SendColorChunks(_plugin, colors, n, cfg.LiveColorCmd);

            _device.WriteArray(cfg.LiveBitmaskCmd,
                new byte[] { (byte)(activeMask & 0xFF), (byte)((activeMask >> 8) & 0xFF) });
        }

        private void ExtLedSendBrightness_Click(object sender, RoutedEventArgs e)
        {
            if (_device == null) return;
            var (cfg, slider) = ((DiagLedCfg, Slider))((Button)sender).Tag;
            _device.WriteSetting(cfg.BrightnessCmd, (int)slider.Value);
        }

        private void ExtLedSendMode_Click(object sender, RoutedEventArgs e)
        {
            if (_device == null) return;
            var (cfg, modeCombo) = ((DiagLedCfg, ComboBox))((Button)sender).Tag;
            if (cfg.ModeCmd == null) return;
            if (modeCombo.SelectedIndex < 0) return;
            _device.WriteSetting(cfg.ModeCmd, modeCombo.SelectedIndex);
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
                _plugin.ApplyTelemetrySettings();
                _plugin.SaveSettings();
                UpdateTelemetryProfileInfo();
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
            _plugin.ApplyTelemetrySettings();
            _plugin.SaveSettings();

            _suppressEvents = true;
            string label = "[Custom: " + System.IO.Path.GetFileName(dlg.FileName) + "]";
            for (int i = TelemetryProfileCombo.Items.Count - 1; i >= 0; i--)
                if (TelemetryProfileCombo.Items[i]?.ToString()?.StartsWith("[Custom:") == true)
                    TelemetryProfileCombo.Items.RemoveAt(i);
            TelemetryProfileCombo.Items.Add(label);
            TelemetryProfileCombo.SelectedIndex = TelemetryProfileCombo.Items.Count - 1;
            _suppressEvents = false;

            UpdateTelemetryProfileInfo();
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
    }
}
