using System.Collections.Generic;
using System.IO;
using MozaPlugin.Telemetry;
using Xunit;

namespace MozaPlugin.Tests.Telemetry
{
    public class DashboardProfileStoreMappingTests
    {
        private static MultiStreamProfile MakeProfile()
        {
            var tier = new DashboardProfile
            {
                Name = "test",
                PackageLevel = 30,
                TotalBits = 16,
                TotalBytes = 2,
            };
            tier.Channels.Add(new ChannelDefinition
            {
                Name = "Rpm",
                Url = "v1/gameData/Rpm",
                Compression = "float_001",
                BitWidth = 10,
                SimHubField = SimHubField.Rpms,
                PackageLevel = 30,
            });
            tier.Channels.Add(new ChannelDefinition
            {
                Name = "Speed",
                Url = "v1/gameData/SpeedKmh",
                Compression = "int10",
                BitWidth = 10,
                SimHubField = SimHubField.SpeedKmh,
                PackageLevel = 30,
            });
            return new MultiStreamProfile { Name = "test", Tiers = { tier } };
        }

        [Fact]
        public void ApplyUserMappings_overrides_only_matching_url()
        {
            var p = MakeProfile();
            var overrides = new Dictionary<string, string>
            {
                ["v1/gameData/Rpm"] = "DataCorePlugin.GameData.CustomRpm",
            };
            DashboardProfileStore.ApplyUserMappings(p, overrides);

            var rpm = p.Tiers[0].Channels[0];
            var speed = p.Tiers[0].Channels[1];
            Assert.Equal("DataCorePlugin.GameData.CustomRpm", rpm.SimHubProperty);
            Assert.Equal("", speed.SimHubProperty);
        }

        [Fact]
        public void ApplyUserMappings_preserves_compression_and_bits()
        {
            var p = MakeProfile();
            var overrides = new Dictionary<string, string>
            {
                ["v1/gameData/Rpm"] = "DataCorePlugin.GameData.X",
            };
            DashboardProfileStore.ApplyUserMappings(p, overrides);

            var rpm = p.Tiers[0].Channels[0];
            Assert.Equal("float_001", rpm.Compression);
            Assert.Equal(10, rpm.BitWidth);
            Assert.Equal(30, rpm.PackageLevel);
        }

        [Fact]
        public void ApplyUserMappings_empty_value_preserves_default()
        {
            var p = MakeProfile();
            p.Tiers[0].Channels[0].SimHubProperty = "DataCorePlugin.GameData.Default";

            var overrides = new Dictionary<string, string> { ["v1/gameData/Rpm"] = "" };
            DashboardProfileStore.ApplyUserMappings(p, overrides);
            // Empty override is a no-op so the channel keeps its default.
            Assert.Equal("DataCorePlugin.GameData.Default", p.Tiers[0].Channels[0].SimHubProperty);
        }

        [Fact]
        public void ApplyUserMappings_null_overrides_is_noop()
        {
            var p = MakeProfile();
            DashboardProfileStore.ApplyUserMappings(p, null);
            Assert.Equal("", p.Tiers[0].Channels[0].SimHubProperty);
        }

        [Fact]
        public void GetDashboardKey_builtin_uses_profile_name()
        {
            var p = new MultiStreamProfile { Name = "Formula 1" };
            Assert.Equal("builtin:Formula 1", DashboardProfileStore.GetDashboardKey(null, p));
            Assert.Equal("builtin:Formula 1", DashboardProfileStore.GetDashboardKey("", p));
        }

        [Fact]
        public void GetDashboardKey_file_includes_filename_and_content_hash()
        {
            string path1 = Path.GetTempFileName();
            string path2 = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path1, "content-A");
                File.WriteAllText(path2, "content-B");

                var p = new MultiStreamProfile { Name = "ignored" };
                string k1 = DashboardProfileStore.GetDashboardKey(path1, p);
                string k2 = DashboardProfileStore.GetDashboardKey(path2, p);

                Assert.StartsWith("file:", k1);
                Assert.StartsWith("file:", k2);
                Assert.NotEqual(k1, k2);
                Assert.Contains(Path.GetFileName(path1), k1);
            }
            finally
            {
                File.Delete(path1); File.Delete(path2);
            }
        }

        [Fact]
        public void GetDashboardKey_same_content_same_filename_matches()
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "stable-content");
                var p = new MultiStreamProfile { Name = "x" };
                string a = DashboardProfileStore.GetDashboardKey(path, p);
                string b = DashboardProfileStore.GetDashboardKey(path, p);
                Assert.Equal(a, b);
            }
            finally { File.Delete(path); }
        }
    }
}
