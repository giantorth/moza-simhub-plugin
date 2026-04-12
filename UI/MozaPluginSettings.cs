namespace MozaPlugin
{
    /// <summary>
    /// Persisted plugin settings. Saved/loaded via SimHub's ReadCommonSettings/SaveCommonSettings.
    /// Stores values that the wheel doesn't retain between sessions.
    /// </summary>
    public class MozaPluginSettings
    {
        // Wheel LED mode settings (-1 = not yet saved)
        public int WheelTelemetryMode { get; set; } = -1;
        public int WheelIdleEffect { get; set; } = -1;
        public int WheelButtonsIdleEffect { get; set; } = -1;

        // ES/Old wheel mode settings (-1 = not yet saved)
        public int WheelRpmIndicatorMode { get; set; } = -1;
        public int WheelRpmDisplayMode { get; set; } = -1;

        // Brightness settings (-1 = not yet saved; defaults: new wheel/dash=100, old wheel=15)
        public int WheelRpmBrightness { get; set; } = 100;
        public int WheelButtonsBrightness { get; set; } = 100;
        public int WheelFlagsBrightness { get; set; } = 100;
        public int WheelESRpmBrightness { get; set; } = 15;
        public int DashRpmBrightness { get; set; } = 100;
        public int DashFlagsBrightness { get; set; } = 100;

        // Blink colors (write-only, can't be polled — persisted here)
        // Packed as R<<16 | G<<8 | B, null = defaults not yet customized
        public int[]? WheelRpmBlinkColors { get; set; }
        public int[]? DashRpmBlinkColors { get; set; }

        // Connection enabled (persisted toggle)
        public bool ConnectionEnabled { get; set; } = true;

        // Whether to automatically apply profile settings on launch
        public bool AutoApplyProfileOnLaunch { get; set; } = true;

        // When true, only send LED updates to wheel when data actually changed (ignores SimHub forceRefresh).
        // Fixes flickering on some non-ES wheels. When false, respects SimHub's refresh cycle.
        public bool LimitWheelUpdates { get; set; } = true;

        // When true, resend LED state to wheel every ~1 second even if unchanged.
        // Some ES wheels need this to stay in telemetry mode.
        public bool WheelKeepalive { get; set; } = true;

        // When true, always resend the LED bitmask alongside color updates even if the bitmask
        // value hasn't changed. Fixes wheels that don't pick up new colors without a bitmask write.
        public bool AlwaysResendBitmask { get; set; } = false;

        // ===== Profile system (SimHub native) =====
        public MozaProfileStore ProfileStore { get; set; } = new MozaProfileStore();

        // ===== Dashboard Telemetry =====
        public bool TelemetryEnabled { get; set; } = false;

        // Name of the active dashboard profile (empty = use first available)
        public string TelemetryProfileName { get; set; } = "";

        // User-loaded .mzdash file path (empty = use builtin profile)
        public string TelemetryMzdashPath { get; set; } = "";

        // Byte limit override (0 = auto from profile)
        public int TelemetryByteLimitOverride { get; set; } = 0;

        // SerialStream port number used as flag byte in telemetry frames.
        // 0x02 is the first port Pithouse allocates after wheel power-on.
        public byte TelemetryFlagByte { get; set; } = 0x02;

        // Telemetry send rate in Hz
        public int TelemetrySendRateHz { get; set; } = 20;

        // Whether to send the 0x40/28:02 telemetry mode frame periodically
        public bool TelemetrySendModeFrame { get; set; } = true;

        // Whether to send the 0x2D/F5:31 sequence counter to the base (~30 Hz)
        public bool TelemetrySendSequenceCounter { get; set; } = true;
    }
}
