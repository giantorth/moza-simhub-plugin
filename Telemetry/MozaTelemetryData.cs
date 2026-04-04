using System;

namespace MozaTelemetryPlugin
{
    /// <summary>
    /// Holds the latest values read from Moza hardware.
    /// </summary>
    public class MozaTelemetryData
    {
        // Connection status
        public volatile bool IsBaseConnected;

        // Temperatures (raw / 100 = degrees C from device)
        public volatile int McuTemp;
        public volatile int MosfetTemp;
        public volatile int MotorTemp;
        public volatile bool UseFahrenheit;

        // State
        public volatile int BaseState;
        public volatile int BaseStateError;

        // Core settings
        public volatile int Limit;
        public volatile int MaxAngle;
        public volatile int FfbStrength;
        public volatile int Torque;
        public volatile int Speed;

        // Wheelbase effects
        public volatile int Damper;
        public volatile int Friction;
        public volatile int Inertia;
        public volatile int Spring;

        // Protection
        public volatile int Protection;
        public volatile int ProtectionMode;
        public volatile int NaturalInertia;

        // High speed damping
        public volatile int SpeedDamping;
        public volatile int SpeedDampingPoint;

        // Soft limit
        public volatile int SoftLimitStiffness;
        public volatile int SoftLimitStrength;
        public volatile int SoftLimitRetain;

        // FFB misc
        public volatile int FfbReverse;
        public volatile int FfbDisable;
        public volatile int TempStrategy;

        // Game effects
        public volatile int GameDamper;
        public volatile int GameFriction;
        public volatile int GameInertia;
        public volatile int GameSpring;

        // Main device
        public volatile int WorkMode;
        public volatile int LedStatus;
        public volatile int Interpolation;

        // ===== Wheel LED settings =====
        public volatile int WheelTelemetryMode;     // 0=Off, 1=Telemetry, 2=Static
        public volatile int WheelTelemetryIdleEffect;
        public volatile int WheelButtonsIdleEffect;
        public volatile int WheelRpmBrightness;
        public volatile int WheelButtonsBrightness;
        public volatile int WheelFlagsBrightness;
        public volatile int WheelRpmMode;           // 0=Percent, 1=RPM
        public volatile int WheelRpmInterval;       // Blink interval ms
        public volatile int WheelIdleMode;
        public volatile int WheelIdleTimeout;
        public volatile int WheelIdleSpeed;
        public volatile int WheelPaddlesMode;
        public volatile int WheelClutchPoint;
        public volatile int WheelKnobMode;
        public volatile int WheelRpmDisplayMode;

        // Wheel RPM threshold values (10 LEDs)
        public readonly int[] WheelRpmValues = new int[10];

        // Wheel RPM colors (10 LEDs, [R, G, B] each)
        public readonly byte[][] WheelRpmColors = InitRpmColorArray();
        public readonly byte[][] WheelButtonColors = InitColorArray(14);
        public readonly byte[][] WheelFlagColors = InitFlagColorArray();
        public readonly byte[] WheelIdleColor = new byte[] { 255, 255, 255 };

        // ES wheel
        public volatile int WheelESRpmBrightness;
        public readonly byte[][] WheelESRpmColors = InitRpmColorArray();
        public volatile int WheelRpmIndicatorMode;

        // ===== Dash LED settings =====
        public volatile int DashRpmIndicatorMode;
        public volatile int DashFlagsIndicatorMode;
        public volatile int DashRpmDisplayMode;
        public volatile int DashRpmMode;            // 0=Percent, 1=RPM
        public volatile int DashRpmBrightness;
        public volatile int DashFlagsBrightness;
        public volatile int DashRpmInterval;

        public readonly int[] DashRpmValues = new int[10];
        public readonly byte[][] DashRpmColors = InitRpmColorArray();
        public readonly byte[][] DashFlagColors = InitFlagColorArray();

        // RPM timings array (percent mode, 10 values)
        public readonly byte[] WheelRpmTimings = new byte[10];
        public readonly byte[] DashRpmTimings = new byte[10];

        // ===== Handbrake settings =====
        public volatile int HandbrakeDirection;      // 0=Normal, 1=Reversed
        public volatile int HandbrakeMode;           // 0=Axis, 1=Button
        public volatile int HandbrakeButtonThreshold; // 0-100 (percent)

        private static byte[][] InitColorArray(int count)
        {
            var arr = new byte[count][];
            for (int i = 0; i < count; i++)
                arr[i] = new byte[] { 0, 0, 0 };
            return arr;
        }

        /// <summary>
        /// Default RPM LED colors: 1-3 green, 4-7 red, 8-10 magenta.
        /// </summary>
        private static byte[][] InitRpmColorArray()
        {
            return new byte[][]
            {
                new byte[] { 0, 255, 0 }, new byte[] { 0, 255, 0 }, new byte[] { 0, 255, 0 },
                new byte[] { 255, 0, 0 }, new byte[] { 255, 0, 0 }, new byte[] { 255, 0, 0 }, new byte[] { 255, 0, 0 },
                new byte[] { 255, 0, 255 }, new byte[] { 255, 0, 255 }, new byte[] { 255, 0, 255 },
            };
        }

        /// <summary>
        /// Default flag LED colors: all magenta.
        /// </summary>
        private static byte[][] InitFlagColorArray()
        {
            var arr = new byte[6][];
            for (int i = 0; i < 6; i++)
                arr[i] = new byte[] { 255, 0, 255 };
            return arr;
        }

        public void UpdateFromCommand(string commandName, int value)
        {
            switch (commandName)
            {
                // Temperatures
                case "base-mcu-temp":       McuTemp = value; IsBaseConnected = true; break;
                case "base-mosfet-temp":    MosfetTemp = value; break;
                case "base-motor-temp":     MotorTemp = value; break;

                // State
                case "base-state":          BaseState = value; break;
                case "base-state-err":      BaseStateError = value; break;

                // Core settings
                case "base-limit":          Limit = value; break;
                case "base-max-angle":      MaxAngle = value; break;
                case "base-ffb-strength":   FfbStrength = value; break;
                case "base-torque":         Torque = value; break;
                case "base-speed":          Speed = value; break;

                // Effects
                case "base-damper":         Damper = value; break;
                case "base-friction":       Friction = value; break;
                case "base-inertia":        Inertia = value; break;
                case "base-spring":         Spring = value; break;

                // Protection
                case "base-protection":         Protection = value; break;
                case "base-protection-mode":    ProtectionMode = value; break;
                case "base-natural-inertia":    NaturalInertia = value; break;

                // High speed damping
                case "base-speed-damping":       SpeedDamping = value; break;
                case "base-speed-damping-point": SpeedDampingPoint = value; break;

                // Soft limit
                case "base-soft-limit-stiffness": SoftLimitStiffness = value; break;
                case "base-soft-limit-strength":  SoftLimitStrength = value; break;
                case "base-soft-limit-retain":    SoftLimitRetain = value; break;

                // FFB misc
                case "base-ffb-reverse":    FfbReverse = value; break;
                case "base-ffb-disable":    FfbDisable = value; break;
                case "base-temp-strategy":  TempStrategy = value; break;

                // Game effects
                case "main-get-damper-gain":   GameDamper = value; break;
                case "main-get-friction-gain": GameFriction = value; break;
                case "main-get-inertia-gain":  GameInertia = value; break;
                case "main-get-spring-gain":   GameSpring = value; break;

                // Main device
                case "main-get-work-mode":     WorkMode = value; break;
                case "main-get-led-status":    LedStatus = value; break;
                case "main-get-interpolation": Interpolation = value; break;

                // Wheel LED settings
                case "wheel-telemetry-mode":        WheelTelemetryMode = value; break;
                case "wheel-telemetry-idle-effect":  WheelTelemetryIdleEffect = value; break;
                case "wheel-buttons-idle-effect":    WheelButtonsIdleEffect = value; break;
                case "wheel-rpm-brightness":         WheelRpmBrightness = value; break;
                case "wheel-buttons-brightness":     WheelButtonsBrightness = value; break;
                case "wheel-flags-brightness":       WheelFlagsBrightness = value; break;
                case "wheel-rpm-mode":               WheelRpmMode = value; break;
                case "wheel-rpm-interval":           WheelRpmInterval = value; break;
                case "wheel-idle-mode":              WheelIdleMode = value; break;
                case "wheel-idle-timeout":           WheelIdleTimeout = value; break;
                case "wheel-idle-speed":             WheelIdleSpeed = value; break;
                case "wheel-paddles-mode":           WheelPaddlesMode = value; break;
                case "wheel-clutch-point":           WheelClutchPoint = value; break;
                case "wheel-knob-mode":              WheelKnobMode = value; break;
                case "wheel-rpm-indicator-mode":     WheelRpmIndicatorMode = value - 1; break; // raw 1/2/3 → display 0/1/2
                case "wheel-get-rpm-display-mode":  WheelRpmDisplayMode = value; break;
                case "wheel-old-rpm-brightness":     WheelESRpmBrightness = value; break;

                // Dash settings
                case "dash-rpm-indicator-mode":  DashRpmIndicatorMode = value; break;
                case "dash-flags-indicator-mode": DashFlagsIndicatorMode = value; break;
                case "dash-rpm-display-mode":    DashRpmDisplayMode = value; break;
                case "dash-rpm-mode":            DashRpmMode = value; break;
                case "dash-rpm-brightness":      DashRpmBrightness = value; break;
                case "dash-flags-brightness":    DashFlagsBrightness = value; break;
                case "dash-rpm-interval":        DashRpmInterval = value; break;

                // Handbrake settings
                case "handbrake-direction":        HandbrakeDirection = value; break;
                case "handbrake-mode":             HandbrakeMode = value; break;
                case "handbrake-button-threshold": HandbrakeButtonThreshold = value; break;
            }

            // Wheel RPM values
            if (commandName.StartsWith("wheel-rpm-value") && commandName.Length == 16)
            {
                int idx = commandName[15] - '1';
                if (commandName.Length == 17) idx = 9; // value10
                else idx = commandName[15] - '1';
                if (idx >= 0 && idx < 10) WheelRpmValues[idx] = value;
            }
            // Dash RPM values
            else if (commandName.StartsWith("dash-rpm-value") && commandName.Length >= 15)
            {
                if (int.TryParse(commandName.Substring(14), out int num) && num >= 1 && num <= 10)
                    DashRpmValues[num - 1] = value;
            }
        }

        /// <summary>
        /// Update from a parsed array response (colors, timings).
        /// </summary>
        public void UpdateFromArray(string commandName, byte[] data)
        {
            if (data == null || data.Length < 3) return;

            // Wheel RPM colors
            if (commandName.StartsWith("wheel-rpm-color") && !commandName.Contains("blink"))
            {
                int idx = ParseTrailingIndex(commandName, "wheel-rpm-color");
                if (idx >= 0 && idx < 10)
                    SetColor(WheelRpmColors[idx], data);
            }
            // Wheel button colors
            else if (commandName.StartsWith("wheel-button-color"))
            {
                int idx = ParseTrailingIndex(commandName, "wheel-button-color");
                if (idx >= 0 && idx < 14)
                    SetColor(WheelButtonColors[idx], data);
            }
            // Wheel flag colors
            else if (commandName.StartsWith("wheel-flag-color"))
            {
                int idx = ParseTrailingIndex(commandName, "wheel-flag-color");
                if (idx >= 0 && idx < 6)
                    SetColor(WheelFlagColors[idx], data);
            }
            // Old wheel RPM colors
            else if (commandName.StartsWith("wheel-old-rpm-color"))
            {
                int idx = ParseTrailingIndex(commandName, "wheel-old-rpm-color");
                if (idx >= 0 && idx < 10)
                    SetColor(WheelESRpmColors[idx], data);
            }
            // Wheel idle color
            else if (commandName == "wheel-idle-color")
            {
                SetColor(WheelIdleColor, data);
            }
            // Dash RPM colors
            else if (commandName.StartsWith("dash-rpm-color") && !commandName.Contains("blink"))
            {
                int idx = ParseTrailingIndex(commandName, "dash-rpm-color");
                if (idx >= 0 && idx < 10)
                    SetColor(DashRpmColors[idx], data);
            }
            // Dash flag colors
            else if (commandName.StartsWith("dash-flag-color") && commandName != "dash-flag-colors")
            {
                int idx = ParseTrailingIndex(commandName, "dash-flag-color");
                if (idx >= 0 && idx < 6)
                    SetColor(DashFlagColors[idx], data);
            }
            // Wheel RPM timings (10-byte array)
            else if (commandName == "wheel-rpm-timings" && data.Length >= 10)
            {
                Array.Copy(data, WheelRpmTimings, 10);
            }
            // Dash RPM timings (10-byte array)
            else if (commandName == "dash-rpm-timings" && data.Length >= 10)
            {
                Array.Copy(data, DashRpmTimings, 10);
            }
        }

        private static int ParseTrailingIndex(string commandName, string prefix)
        {
            var numStr = commandName.Substring(prefix.Length);
            if (int.TryParse(numStr, out int num))
                return num - 1; // Convert 1-based to 0-based
            return -1;
        }

        private static void SetColor(byte[] target, byte[] source)
        {
            target[0] = source[0];
            target[1] = source[1];
            target[2] = source[2];
        }
    }
}
