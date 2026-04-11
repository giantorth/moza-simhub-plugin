using System;

namespace MozaPlugin.Devices
{
    /// <summary>
    /// Wheel-specific settings stored in SimHub device profiles.
    /// Serialized to/from JSON via GetSettings()/SetSettings() on the device extension.
    /// Uses -1 sentinel for "not included" (same convention as MozaProfile).
    /// Colors packed as R&lt;&lt;16 | G&lt;&lt;8 | B.
    /// </summary>
    public class MozaWheelExtensionSettings
    {
        // Wheel LED mode
        public int WheelTelemetryMode { get; set; } = -1;
        public int WheelIdleEffect { get; set; } = -1;
        public int WheelButtonsIdleEffect { get; set; } = -1;

        // Brightness (new wheels 0-100, ES wheels 0-15)
        public int WheelRpmBrightness { get; set; } = 100;
        public int WheelButtonsBrightness { get; set; } = 100;
        public int WheelFlagsBrightness { get; set; } = 100;
        public int WheelESRpmBrightness { get; set; } = 15;

        // ES/Old wheel
        public int WheelRpmIndicatorMode { get; set; } = -1;
        public int WheelRpmDisplayMode { get; set; } = -1;

        // Dashboard telemetry (per-wheel-profile)
        public bool TelemetrySettingsPresent { get; set; } = false;
        public bool TelemetryEnabled { get; set; } = false;
        public string TelemetryProfileName { get; set; } = "";
        public string TelemetryMzdashPath { get; set; } = "";

        // Color arrays (packed as R<<16 | G<<8 | B)
        public int[]? WheelRpmColors { get; set; }
        public int[]? WheelRpmBlinkColors { get; set; }
        public int[]? WheelButtonColors { get; set; }
        public int[]? WheelFlagColors { get; set; }
        public int[]? WheelIdleColor { get; set; }
        public int[]? WheelESRpmColors { get; set; }

        /// <summary>
        /// Capture current wheel state from the plugin.
        /// </summary>
        public void CaptureFromCurrent(MozaPluginSettings settings, MozaData data)
        {
            WheelTelemetryMode = settings.WheelTelemetryMode;
            WheelIdleEffect = settings.WheelIdleEffect;
            WheelButtonsIdleEffect = settings.WheelButtonsIdleEffect;
            WheelRpmBrightness = settings.WheelRpmBrightness;
            WheelButtonsBrightness = settings.WheelButtonsBrightness;
            WheelFlagsBrightness = settings.WheelFlagsBrightness;
            WheelESRpmBrightness = settings.WheelESRpmBrightness;
            WheelRpmIndicatorMode = settings.WheelRpmIndicatorMode;
            WheelRpmDisplayMode = settings.WheelRpmDisplayMode;

            TelemetrySettingsPresent = true;
            TelemetryEnabled = settings.TelemetryEnabled;
            TelemetryProfileName = settings.TelemetryProfileName;
            TelemetryMzdashPath = settings.TelemetryMzdashPath;

            WheelRpmColors = MozaProfile.PackColors(data.WheelRpmColors);
            WheelRpmBlinkColors = MozaProfile.PackColors(data.WheelRpmBlinkColors);
            WheelButtonColors = MozaProfile.PackColors(data.WheelButtonColors);
            WheelFlagColors = MozaProfile.PackColors(data.WheelFlagColors);
            WheelIdleColor = new[] { MozaProfile.PackColor(data.WheelIdleColor) };
            WheelESRpmColors = MozaProfile.PackColors(data.WheelESRpmColors);
        }

        /// <summary>
        /// Apply these settings to the plugin's settings and data model.
        /// Does NOT write to hardware — caller is responsible for that.
        /// </summary>
        public void ApplyTo(MozaPluginSettings settings, MozaData data)
        {
            if (WheelTelemetryMode >= 0) settings.WheelTelemetryMode = WheelTelemetryMode;
            if (WheelIdleEffect >= 0) settings.WheelIdleEffect = WheelIdleEffect;
            if (WheelButtonsIdleEffect >= 0) settings.WheelButtonsIdleEffect = WheelButtonsIdleEffect;
            if (WheelRpmBrightness >= 0) settings.WheelRpmBrightness = WheelRpmBrightness;
            if (WheelButtonsBrightness >= 0) settings.WheelButtonsBrightness = WheelButtonsBrightness;
            if (WheelFlagsBrightness >= 0) settings.WheelFlagsBrightness = WheelFlagsBrightness;
            if (WheelESRpmBrightness >= 0) settings.WheelESRpmBrightness = WheelESRpmBrightness;
            if (WheelRpmIndicatorMode >= 0) settings.WheelRpmIndicatorMode = WheelRpmIndicatorMode;
            if (WheelRpmDisplayMode >= 0) settings.WheelRpmDisplayMode = WheelRpmDisplayMode;

            if (TelemetrySettingsPresent)
            {
                settings.TelemetryEnabled = TelemetryEnabled;
                settings.TelemetryProfileName = TelemetryProfileName;
                settings.TelemetryMzdashPath = TelemetryMzdashPath;
            }

            MozaProfile.UnpackColorsInto(WheelRpmColors, data.WheelRpmColors);
            MozaProfile.UnpackColorsInto(WheelRpmBlinkColors, data.WheelRpmBlinkColors);
            MozaProfile.UnpackColorsInto(WheelButtonColors, data.WheelButtonColors);
            MozaProfile.UnpackColorsInto(WheelFlagColors, data.WheelFlagColors);
            if (WheelIdleColor != null && WheelIdleColor.Length > 0)
            {
                var rgb = MozaProfile.UnpackColor(WheelIdleColor[0]);
                Array.Copy(rgb, data.WheelIdleColor, 3);
            }
            MozaProfile.UnpackColorsInto(WheelESRpmColors, data.WheelESRpmColors);
        }
    }
}
