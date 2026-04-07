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

        // RPM timing mode (0=Percent, 1=RPM, 2=SimHub)
        public int RpmMode { get; set; }

        // RPM thresholds (percent mode, 10 values 0-99)
        public int[] RpmTimingsPercent { get; set; } = { 65, 69, 72, 75, 78, 80, 83, 85, 88, 91 };

        // RPM thresholds (absolute RPM mode, 10 values)
        public int[] RpmTimingsRpm { get; set; } = { 5400, 5700, 6000, 6300, 6500, 6700, 6900, 7100, 7300, 7600 };

        // Blink interval in ms
        public int RpmBlinkInterval { get; set; } = 250;

        // RPM slider range (absolute RPM mode)
        public int WheelRpmRangeMin { get; set; } = 500;
        public int WheelRpmRangeMax { get; set; } = 20000;

        // Button telemetry mode (0=Static, 1=Flags)
        public int ButtonTelemetryMode { get; set; }

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
            ButtonTelemetryMode = settings.ButtonTelemetryMode;

            RpmMode = settings.RpmMode;
            RpmTimingsPercent = (int[])settings.RpmTimingsPercent.Clone();
            RpmTimingsRpm = (int[])settings.RpmTimingsRpm.Clone();
            RpmBlinkInterval = settings.RpmBlinkInterval;
            WheelRpmRangeMin = settings.WheelRpmRangeMin;
            WheelRpmRangeMax = settings.WheelRpmRangeMax;

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
            if (ButtonTelemetryMode >= 0) settings.ButtonTelemetryMode = ButtonTelemetryMode;

            settings.RpmMode = RpmMode;
            if (RpmTimingsPercent != null) settings.RpmTimingsPercent = (int[])RpmTimingsPercent.Clone();
            if (RpmTimingsRpm != null) settings.RpmTimingsRpm = (int[])RpmTimingsRpm.Clone();
            settings.RpmBlinkInterval = RpmBlinkInterval;
            settings.WheelRpmRangeMin = WheelRpmRangeMin;
            settings.WheelRpmRangeMax = WheelRpmRangeMax;

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
