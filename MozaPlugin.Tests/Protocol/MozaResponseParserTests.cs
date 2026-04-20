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

        [Fact]
        public void Parse_WheelClutchPointResponse_Recognized()
        {
            // From usb-capture/cs-to-vgs-wheel.ndjson:
            //   in group=0xc0 device=0x71 cmd=09:28 (clutch-point = 40)
            byte[] response = { 0xC0, 0x71, 0x09, 0x28 };
            var parsed = MozaResponseParser.Parse(response);
            Assert.NotNull(parsed);
            Assert.Equal("wheel-clutch-point", parsed!.Value.Name);
            Assert.Equal(40, parsed.Value.IntValue);
        }

        [Fact]
        public void Parse_WheelPaddlesModeResponse_Recognized()
        {
            // group 0xC0 = toggled 0x40 = 64 (wheel read). cmd id [3], value 2.
            byte[] response = { 0xC0, 0x71, 0x03, 0x02 };
            var parsed = MozaResponseParser.Parse(response);
            Assert.NotNull(parsed);
            Assert.Equal("wheel-paddles-mode", parsed!.Value.Name);
            Assert.Equal(2, parsed.Value.IntValue);
        }

        [Fact]
        public void Parse_WheelStickMode_NewFirmware1Byte()
        {
            // New firmware returns 1 byte for cmd [5] (PayloadBytes=2 in DB).
            // Value 1 = left stick as D-pad.
            byte[] response = { 0xC0, 0x71, 0x05, 0x01 };
            var parsed = MozaResponseParser.Parse(response);
            Assert.NotNull(parsed);
            Assert.Equal("wheel-stick-mode", parsed!.Value.Name);
            Assert.Equal(1, parsed.Value.IntValue);
            Assert.Equal(1, parsed.Value.PayloadLength);
        }

        [Fact]
        public void Parse_WheelStickMode_OldFirmware2Bytes()
        {
            // Old firmware returns 2 bytes: 0x01 0x00 = 256 (left stick on via *256).
            byte[] response = { 0xC0, 0x71, 0x05, 0x01, 0x00 };
            var parsed = MozaResponseParser.Parse(response);
            Assert.NotNull(parsed);
            Assert.Equal("wheel-stick-mode", parsed!.Value.Name);
            Assert.Equal(256, parsed.Value.IntValue);
            Assert.Equal(2, parsed.Value.PayloadLength);
        }

        [Fact]
        public void Parse_HubBasePower_GroupE4_MappedToHub()
        {
            // Group 0xE4 = 228, remapped to logical 100 with device hint "hub".
            // hub-base-power: cmd [0x02], value 0x0001 → 1
            byte[] response = { 0xE4, 0x00, 0x02, 0x00, 0x01 };
            var parsed = MozaResponseParser.Parse(response);
            Assert.NotNull(parsed);
            Assert.Equal("hub-base-power", parsed!.Value.Name);
            Assert.Equal(1, parsed.Value.IntValue);
        }

        [Fact]
        public void Parse_HubBasePower_Group64_DirectMatch()
        {
            // Group 100 (0x64) matches hub commands directly with "hub" hint.
            byte[] response = { 0x64, 0x00, 0x02, 0x00, 0x01 };
            var parsed = MozaResponseParser.Parse(response);
            Assert.NotNull(parsed);
            Assert.Equal("hub-base-power", parsed!.Value.Name);
        }

        [Fact]
        public void Parse_FirmwareDebugNoise_0x0E_FromAnyDevice()
        {
            // Debug frames filtered regardless of device id.
            Assert.Null(MozaResponseParser.Parse(new byte[] { 0x0E, 0x12, 0xAA }));
            Assert.Null(MozaResponseParser.Parse(new byte[] { 0x0E, 0x31, 0xBB, 0xCC }));
            Assert.Null(MozaResponseParser.Parse(new byte[] { 0x0E, 0x81, 0x00, 0x01, 0x02 }));
        }

        [Fact]
        public void Parse_DeviceHintGuards_WheelGroupDoesNotMatchBaseCommand()
        {
            // Group 64 (wheel read) only matches wheel-* commands. If only base cmds
            // had cmd id matching, parser still rejects on device hint mismatch.
            // Use wheel group with cmd id that could ambiguously match nothing, expect null.
            byte[] response = { 0xC0, 0x71, 0xFE, 0xEE, 0xEE };
            Assert.Null(MozaResponseParser.Parse(response));
        }
    }
}
