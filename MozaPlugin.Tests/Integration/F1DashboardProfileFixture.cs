using System.Collections.Generic;
using MozaPlugin.Telemetry;

namespace MozaPlugin.Tests.Integration
{
    /// <summary>
    /// Manually constructs the F1 dashboard MultiStreamProfile (matching "m Formula 1.mzdash")
    /// without going through DashboardProfileStore, which depends on SimHub.Logging.
    /// </summary>
    public static class F1DashboardProfileFixture
    {
        public const string Name = "m Formula 1";

        /// <summary>Builds the 30ms tier profile. Channels are listed in alphabetical URL order.</summary>
        public static DashboardProfile BuildTier30ms()
        {
            var channels = new List<ChannelDefinition>
            {
                Channel("Brake",          "v1/gameData/Brake",          "float_001",    10, SimHubField.Brake),
                Channel("CurrentLapTime", "v1/gameData/CurrentLapTime", "float",        32, SimHubField.CurrentLapTimeSeconds),
                Channel("DrsState",       "v1/gameData/DrsState",       "bool",         1,  SimHubField.DrsEnabled),
                Channel("ErsState",       "v1/gameData/ErsState",       "uint3",        4,  SimHubField.ErsPercent),
                Channel("GAP",            "v1/gameData/GAP",            "float",        32, SimHubField.DeltaToSessionBest),
                Channel("Gear",           "v1/gameData/Gear",           "int30",        5,  SimHubField.Gear),
                Channel("Rpm",            "v1/gameData/Rpm",            "uint16_t",     16, SimHubField.Rpms),
                Channel("SpeedKmh",       "v1/gameData/SpeedKmh",       "float_6000_1", 16, SimHubField.SpeedKmh),
                Channel("Throttle",       "v1/gameData/Throttle",       "float_001",    10, SimHubField.Throttle),
            };

            int totalBits = 0;
            foreach (var c in channels) totalBits += c.BitWidth;

            return new DashboardProfile
            {
                Name         = Name,
                Channels     = channels,
                TotalBits    = totalBits,
                TotalBytes   = (totalBits + 7) / 8,
                PackageLevel = 30,
            };
        }

        public static MultiStreamProfile BuildMultiStream()
        {
            return new MultiStreamProfile
            {
                Name      = Name,
                Tiers     = new List<DashboardProfile> { BuildTier30ms() },
                PageCount = 1,
            };
        }

        private static ChannelDefinition Channel(string name, string url, string compression, int bits, SimHubField field) =>
            new ChannelDefinition
            {
                Name         = name,
                Url          = url,
                Compression  = compression,
                BitWidth     = bits,
                SimHubField  = field,
                PackageLevel = 30,
            };
    }
}
