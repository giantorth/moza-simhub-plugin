using System;

namespace GameReaderCommon
{
    /// <summary>
    /// Minimal stub of SimHub's StatusDataBase for test compilation.
    /// Only the properties accessed by GameDataSnapshot.FromStatusData are defined.
    /// </summary>
    public class StatusDataBase
    {
        public double SpeedKmh { get; set; }
        public double Rpms { get; set; }
        public string? Gear { get; set; }
        public double Throttle { get; set; }
        public double Brake { get; set; }
        public TimeSpan BestLapTime { get; set; }
        public TimeSpan CurrentLapTime { get; set; }
        public TimeSpan LastLapTime { get; set; }
        public double? DeltaToSessionBest { get; set; }
        public double FuelPercent { get; set; }
        public int DRSEnabled { get; set; }
        public double ERSPercent { get; set; }
        public double TyreWearFrontLeft { get; set; }
        public double TyreWearFrontRight { get; set; }
        public double TyreWearRearLeft { get; set; }
        public double TyreWearRearRight { get; set; }
    }
}
