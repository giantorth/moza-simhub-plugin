using System;
using System.Windows.Controls;
using Newtonsoft.Json;
using SimHub.Plugins.ProfilesCommon;

namespace MozaTelemetryPlugin
{
    /// <summary>
    /// A named profile snapshot of all Moza device configuration.
    /// Extends SimHub's ProfileBase for native per-game profile switching.
    /// All integer settings use -1 as sentinel for "not included in this profile".
    /// Colors are stored as packed ints (R &lt;&lt; 16 | G &lt;&lt; 8 | B) for clean JSON serialization.
    /// </summary>
    public class MozaProfile : ProfileBase<MozaProfile, MozaProfileStore>, IProfile, IProfile<MozaProfile, MozaProfileStore>
    {
        [JsonIgnore]
        public override Control ProfileContentControl => null!;

        // ===== Base/Motor settings (raw device values from MozaTelemetryData) =====
        public int Limit { get; set; } = -1;               // raw = degrees / 2
        public int FfbStrength { get; set; } = -1;          // raw = percent * 10
        public int Torque { get; set; } = -1;               // percent
        public int Speed { get; set; } = -1;                // raw = percent * 10
        public int Damper { get; set; } = -1;               // raw = percent * 10
        public int Friction { get; set; } = -1;             // raw = percent * 10
        public int Inertia { get; set; } = -1;              // raw = percent * 10
        public int Spring { get; set; } = -1;               // raw = percent * 10
        public int SpeedDamping { get; set; } = -1;
        public int SpeedDampingPoint { get; set; } = -1;
        public int NaturalInertia { get; set; } = -1;
        public int SoftLimitStiffness { get; set; } = -1;   // raw uses formula
        public int SoftLimitRetain { get; set; } = -1;      // 0/1
        public int FfbReverse { get; set; } = -1;           // 0/1
        public int Protection { get; set; } = -1;           // 0/1

        // ===== Game effect gains (raw = percent * 2.55) =====
        public int GameDamper { get; set; } = -1;
        public int GameFriction { get; set; } = -1;
        public int GameInertia { get; set; } = -1;
        public int GameSpring { get; set; } = -1;

        // ===== Work mode =====
        public int WorkMode { get; set; } = -1;             // 0/1

        // ===== Wheel LED settings =====
        public int WheelTelemetryMode { get; set; } = -1;
        public int WheelIdleEffect { get; set; } = -1;
        public int WheelButtonsIdleEffect { get; set; } = -1;
        public int WheelRpmBrightness { get; set; } = -1;
        public int WheelButtonsBrightness { get; set; } = -1;
        public int WheelFlagsBrightness { get; set; } = -1;

        // ===== ES/Old wheel settings =====
        public int WheelRpmIndicatorMode { get; set; } = -1;
        public int WheelRpmDisplayMode { get; set; } = -1;
        public int WheelESRpmBrightness { get; set; } = -1;

        // ===== RPM timing settings =====
        public int RpmMode { get; set; } = -1;
        public int[]? RpmTimingsPercent { get; set; }
        public int[]? RpmTimingsRpm { get; set; }
        public int RpmBlinkInterval { get; set; } = -1;

        // ===== Dashboard settings =====
        public int DashRpmMode { get; set; } = -1;
        public int[]? DashRpmTimingsPercent { get; set; }
        public int[]? DashRpmTimingsRpm { get; set; }
        public int DashRpmBlinkInterval { get; set; } = -1;
        public int DashRpmBrightness { get; set; } = -1;
        public int DashFlagsBrightness { get; set; } = -1;

        // ===== Handbrake settings =====
        public int HandbrakeMode { get; set; } = -1;             // 0=Axis, 1=Button
        public int HandbrakeButtonThreshold { get; set; } = -1;  // 0-100
        public int HandbrakeDirection { get; set; } = -1;        // 0=Normal, 1=Reversed

        // ===== Color arrays (packed as R<<16 | G<<8 | B) =====
        public int[]? WheelRpmColors { get; set; }       // [10]
        public int[]? WheelButtonColors { get; set; }     // [14]
        public int[]? WheelFlagColors { get; set; }       // [6]
        public int[]? WheelIdleColor { get; set; }        // [1]
        public int[]? WheelESRpmColors { get; set; }     // [10]
        public int[]? DashRpmColors { get; set; }         // [10]
        public int[]? DashFlagColors { get; set; }        // [6]

        // ===== ProfileBase abstract implementation =====

        public override void CopyProfilePropertiesFrom(MozaProfile p)
        {
            // Base/Motor
            Limit = p.Limit; FfbStrength = p.FfbStrength; Torque = p.Torque;
            Speed = p.Speed; Damper = p.Damper; Friction = p.Friction;
            Inertia = p.Inertia; Spring = p.Spring;
            SpeedDamping = p.SpeedDamping; SpeedDampingPoint = p.SpeedDampingPoint;
            NaturalInertia = p.NaturalInertia; SoftLimitStiffness = p.SoftLimitStiffness;
            SoftLimitRetain = p.SoftLimitRetain; FfbReverse = p.FfbReverse;
            Protection = p.Protection;

            // Game effects
            GameDamper = p.GameDamper; GameFriction = p.GameFriction;
            GameInertia = p.GameInertia; GameSpring = p.GameSpring;
            WorkMode = p.WorkMode;

            // Wheel LED
            WheelTelemetryMode = p.WheelTelemetryMode; WheelIdleEffect = p.WheelIdleEffect;
            WheelButtonsIdleEffect = p.WheelButtonsIdleEffect;
            WheelRpmBrightness = p.WheelRpmBrightness; WheelButtonsBrightness = p.WheelButtonsBrightness;
            WheelFlagsBrightness = p.WheelFlagsBrightness;

            // ES wheel
            WheelRpmIndicatorMode = p.WheelRpmIndicatorMode; WheelRpmDisplayMode = p.WheelRpmDisplayMode;
            WheelESRpmBrightness = p.WheelESRpmBrightness;

            // RPM timing
            RpmMode = p.RpmMode; RpmBlinkInterval = p.RpmBlinkInterval;
            RpmTimingsPercent = p.RpmTimingsPercent != null ? (int[])p.RpmTimingsPercent.Clone() : null;
            RpmTimingsRpm = p.RpmTimingsRpm != null ? (int[])p.RpmTimingsRpm.Clone() : null;

            // Dashboard
            DashRpmMode = p.DashRpmMode; DashRpmBlinkInterval = p.DashRpmBlinkInterval;
            DashRpmTimingsPercent = p.DashRpmTimingsPercent != null ? (int[])p.DashRpmTimingsPercent.Clone() : null;
            DashRpmTimingsRpm = p.DashRpmTimingsRpm != null ? (int[])p.DashRpmTimingsRpm.Clone() : null;
            DashRpmBrightness = p.DashRpmBrightness; DashFlagsBrightness = p.DashFlagsBrightness;

            // Handbrake
            HandbrakeMode = p.HandbrakeMode;
            HandbrakeButtonThreshold = p.HandbrakeButtonThreshold;
            HandbrakeDirection = p.HandbrakeDirection;

            // Colors (deep copy)
            WheelRpmColors = CloneArray(p.WheelRpmColors);
            WheelButtonColors = CloneArray(p.WheelButtonColors);
            WheelFlagColors = CloneArray(p.WheelFlagColors);
            WheelIdleColor = CloneArray(p.WheelIdleColor);
            WheelESRpmColors = CloneArray(p.WheelESRpmColors);
            DashRpmColors = CloneArray(p.DashRpmColors);
            DashFlagColors = CloneArray(p.DashFlagColors);
        }

        // ===== Capture current state =====

        /// <summary>
        /// Populate this profile by capturing all current device state.
        /// </summary>
        public void CaptureFromCurrent(MozaPluginSettings settings, MozaTelemetryData data)
        {
            // Base/Motor
            Limit = data.Limit; FfbStrength = data.FfbStrength; Torque = data.Torque;
            Speed = data.Speed; Damper = data.Damper; Friction = data.Friction;
            Inertia = data.Inertia; Spring = data.Spring;
            SpeedDamping = data.SpeedDamping; SpeedDampingPoint = data.SpeedDampingPoint;
            NaturalInertia = data.NaturalInertia; SoftLimitStiffness = data.SoftLimitStiffness;
            SoftLimitRetain = data.SoftLimitRetain; FfbReverse = data.FfbReverse;
            Protection = data.Protection;

            // Game effects
            GameDamper = data.GameDamper; GameFriction = data.GameFriction;
            GameInertia = data.GameInertia; GameSpring = data.GameSpring;
            WorkMode = data.WorkMode;

            // Wheel LED (from settings, since these use -1 sentinel)
            WheelTelemetryMode = settings.WheelTelemetryMode;
            WheelIdleEffect = settings.WheelIdleEffect;
            WheelButtonsIdleEffect = settings.WheelButtonsIdleEffect;
            WheelRpmBrightness = settings.WheelRpmBrightness;
            WheelButtonsBrightness = settings.WheelButtonsBrightness;
            WheelFlagsBrightness = settings.WheelFlagsBrightness;

            // ES wheel
            WheelRpmIndicatorMode = settings.WheelRpmIndicatorMode;
            WheelRpmDisplayMode = settings.WheelRpmDisplayMode;
            WheelESRpmBrightness = settings.WheelESRpmBrightness;

            // RPM timings
            RpmMode = settings.RpmMode;
            RpmTimingsPercent = (int[])settings.RpmTimingsPercent.Clone();
            RpmTimingsRpm = (int[])settings.RpmTimingsRpm.Clone();
            RpmBlinkInterval = settings.RpmBlinkInterval;

            // Dashboard
            DashRpmMode = settings.DashRpmMode;
            DashRpmTimingsPercent = (int[])settings.DashRpmTimingsPercent.Clone();
            DashRpmTimingsRpm = (int[])settings.DashRpmTimingsRpm.Clone();
            DashRpmBlinkInterval = settings.DashRpmBlinkInterval;
            DashRpmBrightness = settings.DashRpmBrightness;
            DashFlagsBrightness = settings.DashFlagsBrightness;

            // Handbrake
            HandbrakeMode = data.HandbrakeMode;
            HandbrakeButtonThreshold = data.HandbrakeButtonThreshold;
            HandbrakeDirection = data.HandbrakeDirection;

            // Colors
            WheelRpmColors = PackColors(data.WheelRpmColors);
            WheelButtonColors = PackColors(data.WheelButtonColors);
            WheelFlagColors = PackColors(data.WheelFlagColors);
            WheelIdleColor = new[] { PackColor(data.WheelIdleColor) };
            WheelESRpmColors = PackColors(data.WheelESRpmColors);
            DashRpmColors = PackColors(data.DashRpmColors);
            DashFlagColors = PackColors(data.DashFlagColors);
        }

        // ===== Color packing helpers =====

        public static int PackColor(byte[] rgb)
        {
            return (rgb[0] << 16) | (rgb[1] << 8) | rgb[2];
        }

        public static byte[] UnpackColor(int packed)
        {
            return new byte[]
            {
                (byte)((packed >> 16) & 0xFF),
                (byte)((packed >> 8) & 0xFF),
                (byte)(packed & 0xFF)
            };
        }

        public static int[] PackColors(byte[][] colors)
        {
            var packed = new int[colors.Length];
            for (int i = 0; i < colors.Length; i++)
                packed[i] = PackColor(colors[i]);
            return packed;
        }

        public static void UnpackColorsInto(int[]? packed, byte[][] target)
        {
            if (packed == null) return;
            int count = Math.Min(packed.Length, target.Length);
            for (int i = 0; i < count; i++)
            {
                var rgb = UnpackColor(packed[i]);
                target[i][0] = rgb[0];
                target[i][1] = rgb[1];
                target[i][2] = rgb[2];
            }
        }

        private static int[]? CloneArray(int[]? source)
        {
            return source != null ? (int[])source.Clone() : null;
        }
    }
}
