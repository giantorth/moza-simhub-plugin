using MozaPlugin.Protocol;
using MozaPlugin.Telemetry;
using MozaPlugin.Tests.Integration;
using Xunit;

namespace MozaPlugin.Tests.Telemetry
{
    public class TelemetryFrameBuilderTests
    {
        [Fact]
        public void BuildFrameFromSnapshot_Header_IsCorrect()
        {
            var profile = F1DashboardProfileFixture.BuildTier30ms();
            var builder = new TelemetryFrameBuilder(profile);

            byte[] frame = builder.BuildFrameFromSnapshot(default, 0x08);

            Assert.Equal(0x7E, frame[0]);
            Assert.Equal((byte)(2 + 6 + profile.TotalBytes), frame[1]);
            Assert.Equal(0x43, frame[2]);   // TelemetrySendGroup
            Assert.Equal(0x17, frame[3]);   // DeviceWheel
            Assert.Equal(0x7D, frame[4]);
            Assert.Equal(0x23, frame[5]);
            Assert.Equal(0x32, frame[6]);
            Assert.Equal(0x00, frame[7]);
            Assert.Equal(0x23, frame[8]);
            Assert.Equal(0x32, frame[9]);
            Assert.Equal(0x08, frame[10]);  // flag byte
            Assert.Equal(0x20, frame[11]);
        }

        [Fact]
        public void BuildFrameFromSnapshot_Length_IsHeaderPlusDataPlusChecksum()
        {
            var profile = F1DashboardProfileFixture.BuildTier30ms();
            var builder = new TelemetryFrameBuilder(profile);

            byte[] frame = builder.BuildFrameFromSnapshot(default, 0x08);

            // 12 header + TotalBytes data + 1 checksum
            Assert.Equal(12 + profile.TotalBytes + 1, frame.Length);
        }

        [Fact]
        public void BuildFrameFromSnapshot_Checksum_IsValid()
        {
            var profile = F1DashboardProfileFixture.BuildTier30ms();
            var builder = new TelemetryFrameBuilder(profile);

            var snapshot = new GameDataSnapshot
            {
                SpeedKmh = 120.5,
                Rpms = 8500,
                Gear = 3,
                Throttle = 0.75,
                Brake = 0.0,
                CurrentLapTimeSeconds = 12.34,
                DeltaToSessionBest = -0.5,
                DrsEnabled = 1,
                ErsPercent = 6,
            };

            byte[] frame = builder.BuildFrameFromSnapshot(snapshot, 0x08);

            byte expected = MozaProtocol.CalculateChecksum(frame, frame.Length - 1);
            Assert.Equal(expected, frame[frame.Length - 1]);
        }

        [Fact]
        public void BuildFrameFromSnapshot_ZeroSnapshot_DataRegionIsAllZero()
        {
            var profile = F1DashboardProfileFixture.BuildTier30ms();
            var builder = new TelemetryFrameBuilder(profile);

            byte[] frame = builder.BuildFrameFromSnapshot(default, 0x08);

            // All data bytes between header (12) and checksum (last) should be 0
            for (int i = 12; i < frame.Length - 1; i++)
                Assert.Equal(0, frame[i]);
        }

        [Fact]
        public void BuildFrameFromSnapshot_ReturnsIndependentCopies()
        {
            var profile = F1DashboardProfileFixture.BuildTier30ms();
            var builder = new TelemetryFrameBuilder(profile);

            byte[] a = builder.BuildFrameFromSnapshot(default, 0x08);
            byte[] b = builder.BuildFrameFromSnapshot(default, 0x08);

            Assert.NotSame(a, b);
            a[12] = 0xFF;
            Assert.NotEqual(0xFF, b[12]);
        }

        [Fact]
        public void BuildStubFrame_Is13Bytes_WithValidChecksum()
        {
            byte[] frame = TelemetryFrameBuilder.BuildStubFrame(0x09);

            Assert.Equal(13, frame.Length);
            Assert.Equal(0x7E, frame[0]);
            Assert.Equal((byte)(2 + 6), frame[1]);
            Assert.Equal(0x43, frame[2]);
            Assert.Equal(0x17, frame[3]);
            Assert.Equal(0x09, frame[10]);
            Assert.Equal(0x20, frame[11]);
            Assert.Equal(MozaProtocol.CalculateChecksum(frame, 12), frame[12]);
        }

        [Fact]
        public void BuildFrame_FlagBytePatchedCorrectly()
        {
            var profile = F1DashboardProfileFixture.BuildTier30ms();
            var builder = new TelemetryFrameBuilder(profile);

            byte[] a = builder.BuildFrameFromSnapshot(default, 0x08);
            byte[] b = builder.BuildFrameFromSnapshot(default, 0x09);

            Assert.Equal(0x08, a[10]);
            Assert.Equal(0x09, b[10]);
            // Different flags yield different checksums
            Assert.NotEqual(a[a.Length - 1], b[b.Length - 1]);
        }
    }
}
