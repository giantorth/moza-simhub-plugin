namespace MozaPlugin.Devices
{
    /// <summary>
    /// Wheel-base ambient LED settings stored in SimHub device profiles.
    /// Serialized to/from JSON via GetSettings()/SetSettings() on the device extension.
    /// Uses -1 sentinel for "not included" (same convention as MozaProfile / MozaDashExtensionSettings).
    /// Colors packed as R&lt;&lt;16 | G&lt;&lt;8 | B.
    /// </summary>
    public class MozaBaseExtensionSettings
    {
        public int BaseAmbientBrightness { get; set; } = -1;       // 0..255 wire range
        public int BaseAmbientStandbyMode { get; set; } = -1;      // 0=const, 1=?, 2=breath, 3=cycle, 4=rainbow, 5=flow
        public int BaseAmbientIndicatorState { get; set; } = -1;   // 0/1
        public int BaseAmbientSleepMode { get; set; } = -1;        // 0/1
        public int BaseAmbientSleepTimeout { get; set; } = -1;
        public int BaseAmbientStartupColor { get; set; } = -1;     // packed RGB
        public int BaseAmbientShutdownColor { get; set; } = -1;    // packed RGB

        /// <summary>
        /// Capture current base ambient state from the plugin.
        /// </summary>
        public void CaptureFromCurrent(MozaPluginSettings settings, MozaData data)
        {
            BaseAmbientBrightness = settings.BaseAmbientBrightness;
            BaseAmbientStandbyMode = settings.BaseAmbientStandbyMode;
            BaseAmbientIndicatorState = settings.BaseAmbientIndicatorState;
            BaseAmbientSleepMode = settings.BaseAmbientSleepMode;
            BaseAmbientSleepTimeout = settings.BaseAmbientSleepTimeout;
            BaseAmbientStartupColor = settings.BaseAmbientStartupColor;
            BaseAmbientShutdownColor = settings.BaseAmbientShutdownColor;
        }

        /// <summary>
        /// Apply these settings to the plugin's settings and data model.
        /// Does NOT write to hardware — caller is responsible for that.
        /// </summary>
        public void ApplyTo(MozaPluginSettings settings, MozaData data)
        {
            if (BaseAmbientBrightness >= 0)
            {
                settings.BaseAmbientBrightness = BaseAmbientBrightness;
                data.BaseAmbientBrightness = BaseAmbientBrightness;
            }
            if (BaseAmbientStandbyMode >= 0)
            {
                settings.BaseAmbientStandbyMode = BaseAmbientStandbyMode;
                data.BaseAmbientStandbyMode = BaseAmbientStandbyMode;
            }
            if (BaseAmbientIndicatorState >= 0)
            {
                settings.BaseAmbientIndicatorState = BaseAmbientIndicatorState;
                data.BaseAmbientIndicatorState = BaseAmbientIndicatorState;
            }
            if (BaseAmbientSleepMode >= 0)
            {
                settings.BaseAmbientSleepMode = BaseAmbientSleepMode;
                data.BaseAmbientSleepMode = BaseAmbientSleepMode;
            }
            if (BaseAmbientSleepTimeout >= 0)
            {
                settings.BaseAmbientSleepTimeout = BaseAmbientSleepTimeout;
                data.BaseAmbientSleepTimeout = BaseAmbientSleepTimeout;
            }
            if (BaseAmbientStartupColor >= 0)
            {
                settings.BaseAmbientStartupColor = BaseAmbientStartupColor;
                UnpackColor(BaseAmbientStartupColor, data.BaseAmbientStartupColor);
            }
            if (BaseAmbientShutdownColor >= 0)
            {
                settings.BaseAmbientShutdownColor = BaseAmbientShutdownColor;
                UnpackColor(BaseAmbientShutdownColor, data.BaseAmbientShutdownColor);
            }
        }

        private static void UnpackColor(int packed, byte[] dst)
        {
            dst[0] = (byte)((packed >> 16) & 0xFF);
            dst[1] = (byte)((packed >> 8) & 0xFF);
            dst[2] = (byte)(packed & 0xFF);
        }
    }
}
