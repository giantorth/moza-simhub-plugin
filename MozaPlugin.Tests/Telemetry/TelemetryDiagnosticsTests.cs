using MozaPlugin.Telemetry;
using Xunit;

namespace MozaPlugin.Tests.Telemetry
{
    public class TelemetryDiagnosticsTests
    {
        [Fact]
        public void BuildTestPattern_Frame0_KnownValues()
        {
            var d = new TelemetryDiagnostics();
            var snap = d.BuildTestPattern(0);

            Assert.Equal(0.0, snap.SpeedKmh);
            Assert.Equal(0.0, snap.Rpms);
            Assert.Equal(1.0, snap.Gear);      // (int)(0*6)+1 = 1
            Assert.Equal(0.0, snap.Throttle);
            Assert.Equal(1.0, snap.Brake);
            Assert.Equal(1.0, snap.DrsEnabled); // frame 0: 0%40 < 20 → on
        }

        [Fact]
        public void BuildTestPattern_Frame100_KnownValues()
        {
            var d = new TelemetryDiagnostics();
            var snap = d.BuildTestPattern(100);

            Assert.Equal(100.0, snap.SpeedKmh);   // 0.5 * 200
            Assert.Equal(4000.0, snap.Rpms);       // 0.5 * 8000
            Assert.Equal(4.0, snap.Gear);          // (int)(0.5*6)+1 = 4
            Assert.Equal(0.5, snap.Throttle);
            Assert.Equal(0.5, snap.Brake);
        }

        [Fact]
        public void BuildTestPattern_WrapsAt200()
        {
            var d = new TelemetryDiagnostics();
            var a = d.BuildTestPattern(0);
            var b = d.BuildTestPattern(200);

            Assert.Equal(a.SpeedKmh, b.SpeedKmh);
            Assert.Equal(a.Rpms,     b.Rpms);
            Assert.Equal(a.Throttle, b.Throttle);
            Assert.Equal(a.Brake,    b.Brake);
        }

        [Fact]
        public void BuildTestPattern_Deterministic()
        {
            var d = new TelemetryDiagnostics();
            var a = d.BuildTestPattern(73);
            var b = d.BuildTestPattern(73);

            Assert.Equal(a.SpeedKmh, b.SpeedKmh);
            Assert.Equal(a.Gear,     b.Gear);
            Assert.Equal(a.Throttle, b.Throttle);
        }

        [Fact]
        public void BuildTestPattern_DrsToggles()
        {
            var d = new TelemetryDiagnostics();

            Assert.Equal(1.0, d.BuildTestPattern(0).DrsEnabled);
            Assert.Equal(1.0, d.BuildTestPattern(19).DrsEnabled);
            Assert.Equal(0.0, d.BuildTestPattern(20).DrsEnabled);
            Assert.Equal(0.0, d.BuildTestPattern(39).DrsEnabled);
            Assert.Equal(1.0, d.BuildTestPattern(40).DrsEnabled);
        }

        [Fact]
        public void RecordFrame_AndGetLog_ReturnsCopies()
        {
            var d = new TelemetryDiagnostics();
            d.RecordFrame(new byte[] { 0x7E, 0x03, 0x28 });
            d.RecordFrame(new byte[] { 0x7E, 0x04, 0x29 });

            var log = d.GetLog();
            Assert.Equal(2, log.Length);
            Assert.Equal(0x28, log[0].Frame[2]);
            Assert.Equal(0x29, log[1].Frame[2]);
        }

        [Fact]
        public void RecordFrame_CapsAt100Entries()
        {
            var d = new TelemetryDiagnostics();
            for (int i = 0; i < 150; i++)
                d.RecordFrame(new byte[] { (byte)i });

            var log = d.GetLog();
            Assert.Equal(100, log.Length);
            // Oldest kept entry is frame 50
            Assert.Equal(50, log[0].Frame[0]);
            Assert.Equal(149, log[99].Frame[0]);
        }
    }
}
