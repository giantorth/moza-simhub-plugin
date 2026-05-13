namespace MozaPlugin.Devices
{
    /// <summary>
    /// User-facing indicator-mode order shared by the wheel and dash settings
    /// controls. Values are the ComboBox SelectedIndex (the XAML lists the items
    /// in this exact order). Device-stored values are bijective with this enum
    /// but differ per device — see <see cref="IndicatorMode"/>.
    /// </summary>
    internal enum IndicatorDisplayMode
    {
        SimHub = 0,
        AlwaysOn = 1,
        Off = 2,
    }

    /// <summary>
    /// Bijective conversion between <see cref="IndicatorDisplayMode"/> (combo
    /// box ordering) and the per-device stored byte. The two devices encode
    /// the same three modes differently, so each device has its own pair.
    ///
    /// ES wheel: firmware sends 1=RPM/2=Off/3=On, normalized by `-1` on read
    /// → stored 0=SimHub, 1=Off, 2=AlwaysOn.
    ///
    /// Dash: stored 0=Off, 1=SimHub, 2=AlwaysOn (no normalization).
    /// </summary>
    internal static class IndicatorMode
    {
        public static IndicatorDisplayMode FromEsStored(int stored) => stored switch
        {
            0 => IndicatorDisplayMode.SimHub,
            1 => IndicatorDisplayMode.Off,
            2 => IndicatorDisplayMode.AlwaysOn,
            _ => IndicatorDisplayMode.SimHub,
        };

        public static int ToEsStored(IndicatorDisplayMode display) => display switch
        {
            IndicatorDisplayMode.SimHub => 0,
            IndicatorDisplayMode.Off => 1,
            IndicatorDisplayMode.AlwaysOn => 2,
            _ => 0,
        };

        public static IndicatorDisplayMode FromDashStored(int stored) => stored switch
        {
            0 => IndicatorDisplayMode.Off,
            1 => IndicatorDisplayMode.SimHub,
            2 => IndicatorDisplayMode.AlwaysOn,
            _ => IndicatorDisplayMode.SimHub,
        };

        public static int ToDashStored(IndicatorDisplayMode display) => display switch
        {
            IndicatorDisplayMode.Off => 0,
            IndicatorDisplayMode.SimHub => 1,
            IndicatorDisplayMode.AlwaysOn => 2,
            _ => 1,
        };
    }
}
