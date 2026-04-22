using System.Collections.Generic;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Curated list of common SimHub property paths used to populate the
    /// channel-mapping ComboBox autocomplete suggestions. Free-form entry
    /// remains allowed — these are hints, not a whitelist.
    /// </summary>
    public static class KnownSimHubProperties
    {
        public static readonly IReadOnlyList<string> Paths = new List<string>
        {
            // Core motion/telemetry
            "DataCorePlugin.GameData.Rpms",
            "DataCorePlugin.GameData.FilteredRpms",
            "DataCorePlugin.GameData.MaxRpm",
            "DataCorePlugin.GameData.SpeedKmh",
            "DataCorePlugin.GameData.FilteredSpeedKmh",
            "DataCorePlugin.GameData.Gear",
            "DataCorePlugin.GameData.Throttle",
            "DataCorePlugin.GameData.Brake",
            "DataCorePlugin.GameData.Clutch",
            "DataCorePlugin.GameData.Handbrake",
            "DataCorePlugin.GameData.SteeringAngle",

            // Lap / session
            "DataCorePlugin.GameData.BestLapTime",
            "DataCorePlugin.GameData.CurrentLapTime",
            "DataCorePlugin.GameData.LastLapTime",
            "DataCorePlugin.GameData.DeltaToSessionBest",
            "DataCorePlugin.GameData.DeltaToBestLap",
            "DataCorePlugin.GameData.CurrentLap",
            "DataCorePlugin.GameData.TotalLaps",
            "DataCorePlugin.GameData.Position",

            // Fuel / ERS / DRS
            "DataCorePlugin.GameData.FuelPercent",
            "DataCorePlugin.GameData.Fuel",
            "DataCorePlugin.GameData.DRSEnabled",
            "DataCorePlugin.GameData.ERSPercent",

            // Engine
            "DataCorePlugin.GameData.OilPressure",
            "DataCorePlugin.GameData.OilTemperature",
            "DataCorePlugin.GameData.WaterTemperature",
            "DataCorePlugin.GameData.TurboPressure",

            // Tyre wear
            "DataCorePlugin.GameData.TyreWearFrontLeft",
            "DataCorePlugin.GameData.TyreWearFrontRight",
            "DataCorePlugin.GameData.TyreWearRearLeft",
            "DataCorePlugin.GameData.TyreWearRearRight",

            // Tyre temps
            "DataCorePlugin.GameData.TyreTemperatureFrontLeft",
            "DataCorePlugin.GameData.TyreTemperatureFrontRight",
            "DataCorePlugin.GameData.TyreTemperatureRearLeft",
            "DataCorePlugin.GameData.TyreTemperatureRearRight",

            // Tyre pressures
            "DataCorePlugin.GameData.TyrePressureFrontLeft",
            "DataCorePlugin.GameData.TyrePressureFrontRight",
            "DataCorePlugin.GameData.TyrePressureRearLeft",
            "DataCorePlugin.GameData.TyrePressureRearRight",

            // Flags
            "DataCorePlugin.GameData.Flag_Checkered",
            "DataCorePlugin.GameData.Flag_Black",
            "DataCorePlugin.GameData.Flag_Orange",
            "DataCorePlugin.GameData.Flag_Yellow",
            "DataCorePlugin.GameData.Flag_Blue",
            "DataCorePlugin.GameData.Flag_White",
            "DataCorePlugin.GameData.Flag_Green",

            // Shift lights
            "DataCorePlugin.GameData.CarSettings_RPMShiftLight1",
            "DataCorePlugin.GameData.CarSettings_RPMShiftLight2",
            "DataCorePlugin.GameData.CarSettings_RPMRedLineReached",
        };
    }
}
