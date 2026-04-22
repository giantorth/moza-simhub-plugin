using System.Collections.Generic;
using System.Linq;

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
        /// Used as the fallback when <see cref="SimHubProperty"/> is empty.
        /// </summary>
        public SimHubField SimHubField { get; set; } = SimHubField.Zero;

        /// <summary>
        /// Full SimHub property path (e.g. "DataCorePlugin.GameData.Rpms") resolved
        /// per-frame via <c>PluginManager.GetPropertyValue</c>. Empty = use SimHubField fallback.
        /// Populated from defaults at load time; user overrides persisted via settings.
        /// </summary>
        public string SimHubProperty { get; set; } = "";

        /// <summary>Telemetry tier (ms update interval, e.g. 30, 500, 2000).</summary>
        public int PackageLevel { get; set; } = 30;
    }

    public class DashboardProfile
    {
        public string Name { get; set; } = "";
        public List<ChannelDefinition> Channels { get; set; } = new List<ChannelDefinition>();
        public int TotalBits { get; set; }
        public int TotalBytes { get; set; }

        /// <summary>Telemetry tier this profile covers (ms interval).</summary>
        public int PackageLevel { get; set; } = 30;

        public override string ToString() =>
            $"{Name} ({Channels.Count} channels, {TotalBytes} bytes)";
    }

    /// <summary>
    /// Concurrent telemetry streams for one dashboard, split by package_level tier.
    /// Tiers are sorted by package_level ascending; flag bytes are FlagByte + tier index.
    /// </summary>
    public class MultiStreamProfile
    {
        public string Name { get; set; } = "";

        /// <summary>
        /// Per-tier profiles, sorted by PackageLevel ascending.
        /// Flag byte offset = index in this list (0, 1, 2, ...).
        /// </summary>
        public List<DashboardProfile> Tiers { get; set; } = new List<DashboardProfile>();

        /// <summary>
        /// Number of pages (children) in the dashboard. Used for 7c:27 display config frames.
        /// Defaults to 1 for profiles that don't come from an mzdash file.
        /// </summary>
        public int PageCount { get; set; } = 1;

        public override string ToString()
        {
            var parts = Tiers.Select(t => $"L{t.PackageLevel}:{t.Channels.Count}ch/{t.TotalBytes}B");
            return $"{Name} ({string.Join(", ", parts)})";
        }
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
