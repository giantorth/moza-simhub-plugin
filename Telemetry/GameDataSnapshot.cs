using System;
using GameReaderCommon;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// A flat snapshot of the game data fields used by the telemetry sender.
    /// Decouples TelemetryFrameBuilder from the SimHub StatusDataBase API,
    /// enabling test patterns and unit testing without live game data.
    /// </summary>
    public struct GameDataSnapshot
    {
        public double SpeedKmh;
        public double Rpms;
        public double Gear;         // 0=R/N, 1=1st, 2=2nd, …
        public double Throttle;     // 0–100
        public double Brake;        // 0–100
        public double BestLapTimeSeconds;
        public double CurrentLapTimeSeconds;
        public double LastLapTimeSeconds;
        public double DeltaToSessionBest;
        public double FuelPercent;  // 0–100
        public double DrsEnabled;   // 0 or 1
        public double ErsPercent;   // 0–100
        public double TyreWearFrontLeft;
        public double TyreWearFrontRight;
        public double TyreWearRearLeft;
        public double TyreWearRearRight;

        /// <summary>Populate from a live StatusDataBase instance.</summary>
        public static GameDataSnapshot FromStatusData(StatusDataBase? data)
        {
            if (data == null) return default;
            return new GameDataSnapshot
            {
                SpeedKmh               = data.SpeedKmh,
                Rpms                   = data.Rpms,
                Gear                   = ParseGear(data.Gear),
                Throttle               = data.Throttle,
                Brake                  = data.Brake,
                BestLapTimeSeconds     = data.BestLapTime.TotalSeconds,
                CurrentLapTimeSeconds  = data.CurrentLapTime.TotalSeconds,
                LastLapTimeSeconds     = data.LastLapTime.TotalSeconds,
                DeltaToSessionBest     = data.DeltaToSessionBest ?? 0.0,
                FuelPercent            = data.FuelPercent,
                DrsEnabled             = data.DRSEnabled != 0 ? 1.0 : 0.0,
                ErsPercent             = data.ERSPercent,
                TyreWearFrontLeft      = data.TyreWearFrontLeft,
                TyreWearFrontRight     = data.TyreWearFrontRight,
                TyreWearRearLeft       = data.TyreWearRearLeft,
                TyreWearRearRight      = data.TyreWearRearRight,
            };
        }

        private static double ParseGear(string? gear)
        {
            if (gear == null || gear.Length == 0) return 0;
            if (gear == "R") return 0; // reverse encoding unknown — send 0
            if (gear == "N") return 0;
            if (int.TryParse(gear, out int n)) return n;
            return 0;
        }

        public double GetField(SimHubField field)
        {
            switch (field)
            {
                case SimHubField.SpeedKmh:              return SpeedKmh;
                case SimHubField.Rpms:                  return Rpms;
                case SimHubField.Gear:                  return Gear;
                case SimHubField.Throttle:              return Throttle;
                case SimHubField.Brake:                 return Brake;
                case SimHubField.BestLapTimeSeconds:    return BestLapTimeSeconds;
                case SimHubField.CurrentLapTimeSeconds: return CurrentLapTimeSeconds;
                case SimHubField.LastLapTimeSeconds:    return LastLapTimeSeconds;
                case SimHubField.DeltaToSessionBest:    return DeltaToSessionBest;
                case SimHubField.FuelPercent:           return FuelPercent;
                case SimHubField.DrsEnabled:            return DrsEnabled;
                case SimHubField.ErsPercent:            return ErsPercent;
                case SimHubField.TyreWearFrontLeft:     return TyreWearFrontLeft;
                case SimHubField.TyreWearFrontRight:    return TyreWearFrontRight;
                case SimHubField.TyreWearRearLeft:      return TyreWearRearLeft;
                case SimHubField.TyreWearRearRight:     return TyreWearRearRight;
                default:                                return 0.0;
            }
        }
    }
}
