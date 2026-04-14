using System;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Dashboard-specific settings stored in SimHub device profiles.
    /// Serialized to/from JSON via GetSettings()/SetSettings() on the device extension.
    /// Uses -1 sentinel for "not included" (same convention as MozaProfile).
    /// Colors packed as R&lt;&lt;16 | G&lt;&lt;8 | B.
    /// </summary>
    public class MozaDashExtensionSettings
    {
        // Brightness (0-100)
        public int DashRpmBrightness { get; set; } = -1;
        public int DashFlagsBrightness { get; set; } = -1;

        // Indicator/display modes
        public int DashRpmIndicatorMode { get; set; } = -1;
        public int DashFlagsIndicatorMode { get; set; } = -1;
        public int DashRpmDisplayMode { get; set; } = -1;

        // Color arrays (packed as R<<16 | G<<8 | B)
        public int[]? DashRpmColors { get; set; }
        public int[]? DashRpmBlinkColors { get; set; }
        public int[]? DashFlagColors { get; set; }

        /// <summary>
        /// Capture current dash state from the plugin.
        /// </summary>
        public void CaptureFromCurrent(MozaPluginSettings settings, MozaData data)
        {
            if (!data.BaseSettingsRead) return;

            DashRpmBrightness = settings.DashRpmBrightness;
            DashFlagsBrightness = settings.DashFlagsBrightness;
            DashRpmIndicatorMode = data.DashRpmIndicatorMode;
            DashFlagsIndicatorMode = data.DashFlagsIndicatorMode;
            DashRpmDisplayMode = data.DashRpmDisplayMode;

            DashRpmColors = MozaProfile.PackColors(data.DashRpmColors);
            DashRpmBlinkColors = settings.DashRpmBlinkColors;
            DashFlagColors = MozaProfile.PackColors(data.DashFlagColors);
        }

        /// <summary>
        /// Apply these settings to the plugin's settings and data model.
        /// Does NOT write to hardware — caller is responsible for that.
        /// </summary>
        public void ApplyTo(MozaPluginSettings settings, MozaData data)
        {
            if (DashRpmBrightness >= 0)
            {
                settings.DashRpmBrightness = DashRpmBrightness;
                data.DashRpmBrightness = DashRpmBrightness;
            }
            if (DashFlagsBrightness >= 0)
            {
                settings.DashFlagsBrightness = DashFlagsBrightness;
                data.DashFlagsBrightness = DashFlagsBrightness;
            }
            if (DashRpmIndicatorMode >= 0) data.DashRpmIndicatorMode = DashRpmIndicatorMode;
            if (DashFlagsIndicatorMode >= 0) data.DashFlagsIndicatorMode = DashFlagsIndicatorMode;
            if (DashRpmDisplayMode >= 0) data.DashRpmDisplayMode = DashRpmDisplayMode;

            MozaProfile.UnpackColorsInto(DashRpmColors, data.DashRpmColors);
            MozaProfile.UnpackColorsInto(DashFlagColors, data.DashFlagColors);
        }
    }
}
