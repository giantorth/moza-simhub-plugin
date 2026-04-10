using System.Collections.Generic;

namespace MozaPlugin.Telemetry
{
    public class ChannelDefinition
    {
        /// <summary>Short display name, e.g. "Brake".</summary>
        public string Name { get; set; } = "";

        /// <summary>Full Moza telemetry URL, e.g. "v1/gameData/Brake".</summary>
        public string Url { get; set; } = "";

        /// <summary>Compression type string, e.g. "float_001".</summary>
        public string Compression { get; set; } = "";

        /// <summary>Bit width for this channel.</summary>
        public int BitWidth { get; set; }

        /// <summary>
        /// How to read the value from SimHub GameData.
        /// One of the SimHubProperty enum values defined in DashboardProfileStore.
        /// </summary>
        public SimHubField SimHubField { get; set; } = SimHubField.Zero;
    }

    public class DashboardProfile
    {
        public string Name { get; set; } = "";
        public List<ChannelDefinition> Channels { get; set; } = new List<ChannelDefinition>();
        public int TotalBits { get; set; }
        public int TotalBytes { get; set; }

        public override string ToString() =>
            $"{Name} ({Channels.Count} channels, {TotalBytes} bytes)";
    }

    /// <summary>
    /// Identifies which SimHub game data field supplies a channel's value.
    /// </summary>
    public enum SimHubField
    {
        Zero = 0,           // Unknown / unsupported — always send 0
        SpeedKmh,
        Rpms,
        Gear,               // SimHub: -1=R, 0=N, 1+=forward gears
        Throttle,           // 0–100
        Brake,              // 0–100
        BestLapTimeSeconds,
        CurrentLapTimeSeconds,
        LastLapTimeSeconds,
        DeltaToSessionBest, // seconds (GAP)
        FuelPercent,        // 0–100
        DrsEnabled,         // bool
        ErsPercent,         // 0–100
        TyreWearFrontLeft,  // 0–100
        TyreWearFrontRight,
        TyreWearRearLeft,
        TyreWearRearRight,
    }
}
