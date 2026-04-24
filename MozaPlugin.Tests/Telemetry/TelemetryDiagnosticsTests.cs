using MozaPlugin.Telemetry;
using Xunit;

namespace MozaPlugin.Tests.Telemetry
{
    public class TelemetryDiagnosticsTests
    {
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
