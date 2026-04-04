namespace MozaPlugin
{
    /// <summary>
    /// Persisted plugin settings. Saved/loaded via SimHub's ReadCommonSettings/SaveCommonSettings.
    /// Stores values that the wheel doesn't retain between sessions.
    /// </summary>
    public class MozaPluginSettings
    {
        // RPM timing thresholds (percent mode, 10 values 0-99) — defaults to Early preset
        public int[] RpmTimingsPercent { get; set; } = { 65, 69, 72, 75, 78, 80, 83, 85, 88, 91 };

        // RPM timing thresholds (absolute RPM mode, 10 values) — defaults to Early preset
        public int[] RpmTimingsRpm { get; set; } = { 5400, 5700, 6000, 6300, 6500, 6700, 6900, 7100, 7300, 7600 };

        // Blink interval in ms
        public int RpmBlinkInterval { get; set; } = 250;

        // RPM mode (0=Percent, 1=RPM)
        public int RpmMode { get; set; }

        // Wheel LED mode settings (-1 = not yet saved)
        public int WheelTelemetryMode { get; set; } = -1;
        public int WheelIdleEffect { get; set; } = -1;
        public int WheelButtonsIdleEffect { get; set; } = -1;

        // ES/Old wheel mode settings (-1 = not yet saved)
        public int WheelRpmIndicatorMode { get; set; } = -1;
        public int WheelRpmDisplayMode { get; set; } = -1;

        // Dashboard RPM timing thresholds (percent mode, 10 values 0-99) — defaults to Early preset
        public int[] DashRpmTimingsPercent { get; set; } = { 65, 69, 72, 75, 78, 80, 83, 85, 88, 91 };

        // Dashboard RPM timing thresholds (absolute RPM mode, 10 values) — defaults to Early preset
        public int[] DashRpmTimingsRpm { get; set; } = { 5400, 5700, 6000, 6300, 6500, 6700, 6900, 7100, 7300, 7600 };

        // Dashboard RPM mode (0=Percent, 1=RPM)
        public int DashRpmMode { get; set; }

        // Dashboard blink interval in ms
        public int DashRpmBlinkInterval { get; set; } = 250;

        // Brightness settings (-1 = not yet saved; defaults: new wheel=100, old/dash=15)
        public int WheelRpmBrightness { get; set; } = 100;
        public int WheelButtonsBrightness { get; set; } = 100;
        public int WheelFlagsBrightness { get; set; } = 100;
        public int WheelESRpmBrightness { get; set; } = 15;
        public int DashRpmBrightness { get; set; } = 15;
        public int DashFlagsBrightness { get; set; } = 15;

        // Connection enabled (persisted toggle)
        public bool ConnectionEnabled { get; set; } = true;

        // Whether to automatically apply profile settings on launch
        public bool AutoApplyProfileOnLaunch { get; set; } = true;

        // ===== Profile system (SimHub native) =====
        public MozaProfileStore ProfileStore { get; set; } = new MozaProfileStore();
    }
}
