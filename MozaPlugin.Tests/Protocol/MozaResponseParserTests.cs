using MozaPlugin.Protocol;
using Xunit;

namespace MozaPlugin.Tests.Protocol
{
    public class MozaResponseParserTests
    {
        [Fact]
        public void Parse_BaseLimitResponse_Recognized()
        {
            // Synthesize response for "base-limit": ReadGroup=40 → response group = 0x80 | 40 = 0xA8
            // Device 19 (0x13) → swapped nibbles = 0x31
            // CommandId = [0x01], payload = [0x01, 0xF4] (BE 500)
            // Response data passed to Parse() is everything AFTER start+length+checksum:
            //   [responseGroup, responseDeviceId, cmdId..., payload...]
            byte[] response = { 0xA8, 0x31, 0x01, 0x01, 0xF4 };

            var parsed = MozaResponseParser.Parse(response);
            Assert.NotNull(parsed);
            Assert.Equal("base-limit", parsed!.Value.Name);
            Assert.Equal(500, parsed.Value.IntValue);
            Assert.Equal(0x13, parsed.Value.DeviceId);
        }

        [Fact]
        public void Parse_TooShort_ReturnsNull()
        {
            Assert.Null(MozaResponseParser.Parse(new byte[] { 0x00, 0x01 }));
            Assert.Null(MozaResponseParser.Parse(new byte[0]));
        }

        [Fact]
        public void Parse_FirmwareDebugNoise_ReturnsNull()
        {
            // Group 0x0E is the firmware debug channel — should be filtered
            byte[] response = { 0x0E, 0x00, 0xAA, 0xBB, 0xCC };
            Assert.Null(MozaResponseParser.Parse(response));
        }

        [Fact]
        public void Parse_UnknownGroup_ReturnsNull()
        {
            // Group 0x99 isn't in any command — no match
            byte[] response = { 0x99, 0x31, 0x99, 0x99, 0x99 };
            Assert.Null(MozaResponseParser.Parse(response));
        }

        [Fact]
        public void Parse_NullData_ReturnsNull()
        {
            Assert.Null(MozaResponseParser.Parse(null!));
        }
    }
}
