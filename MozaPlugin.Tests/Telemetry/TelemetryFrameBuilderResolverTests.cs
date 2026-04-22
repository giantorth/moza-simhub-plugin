using System.Collections.Generic;
using MozaPlugin.Telemetry;
using Xunit;

namespace MozaPlugin.Tests.Telemetry
{
    public class TelemetryFrameBuilderResolverTests
    {
        private static DashboardProfile MakeUint16Profile(string? simHubProperty, SimHubField field)
        {
            var ch = new ChannelDefinition
            {
                Name = "X",
                Url = "v1/gameData/X",
                Compression = "uint16_t",
                BitWidth = 16,
                SimHubField = field,
                SimHubProperty = simHubProperty ?? "",
                PackageLevel = 30,
            };
            return new DashboardProfile
            {
                Name = "test",
                Channels = { ch },
                TotalBits = 16,
                TotalBytes = 2,
                PackageLevel = 30,
            };
        }

        private static int DecodeUint16At12(byte[] frame) => frame[12] | (frame[13] << 8);

        [Fact]
        public void Resolver_is_used_when_SimHubProperty_set()
        {
            var profile = MakeUint16Profile("custom.path", SimHubField.Zero);
            var calls = new List<string>();
            double ResolverFn(string path) { calls.Add(path); return 123.0; }

            var builder = new TelemetryFrameBuilder(profile, ResolverFn);
            var frame = builder.BuildFrameFromSnapshot(default, flagByte: 0x00);

            Assert.Single(calls);
            Assert.Equal("custom.path", calls[0]);
            Assert.Equal(123, DecodeUint16At12(frame));
        }

        [Fact]
        public void Snapshot_field_is_used_when_SimHubProperty_empty()
        {
            var profile = MakeUint16Profile("", SimHubField.Rpms);
            bool called = false;
            double ResolverFn(string _) { called = true; return 0; }

            var builder = new TelemetryFrameBuilder(profile, ResolverFn);
            var snapshot = new GameDataSnapshot { Rpms = 7777 };
            var frame = builder.BuildFrameFromSnapshot(snapshot, flagByte: 0x00);

            Assert.False(called);
            Assert.Equal(7777, DecodeUint16At12(frame));
        }

        [Fact]
        public void Null_resolver_falls_back_to_snapshot_even_if_property_set()
        {
            var profile = MakeUint16Profile("some.path", SimHubField.Rpms);
            var builder = new TelemetryFrameBuilder(profile, propertyResolver: null);
            var snapshot = new GameDataSnapshot { Rpms = 555 };
            var frame = builder.BuildFrameFromSnapshot(snapshot, flagByte: 0x00);
            Assert.Equal(555, DecodeUint16At12(frame));
        }
    }
}
