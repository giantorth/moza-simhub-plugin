using MozaPlugin.Protocol;
using Xunit;

namespace MozaPlugin.Tests.Protocol
{
    public class MozaAb9CommandTests
    {
        [Theory]
        [InlineData("ab9-mech-resistance",  0xD6)]
        [InlineData("ab9-spring",           0xAF)]
        [InlineData("ab9-natural-damping",  0xB0)]
        [InlineData("ab9-natural-friction", 0xB2)]
        [InlineData("ab9-max-torque-limit", 0xA9)]
        public void Slider_BuildWriteMessage_MatchesCapturedFrame(string commandName, byte cmdHi)
        {
            // Captured AB9 slider write (from docs/moza-protocol.md § "AB9 active shifter"):
            //   7E 03 1F 12 <cmdHi> 00 64 <checksum>     for value=100 on each slider
            // payloadLength = cmdId(2) + payload(1) = 3.
            var cmd = MozaCommandDatabase.Get(commandName);
            Assert.NotNull(cmd);
            Assert.Equal(0x1F, cmd!.WriteGroup);
            Assert.Equal(0x1F, cmd.ReadGroup); // mirror of 0x9F response after ToggleBit7

            byte[]? msg = cmd.BuildWriteMessage(MozaProtocol.DeviceAb9, new byte[] { 0x64 });
            Assert.NotNull(msg);

            // Expected layout (no 0x7E in body, so wire form == raw form):
            // [0x7E, 0x03, 0x1F, 0x12, cmdHi, 0x00, 0x64, checksum]
            Assert.Equal(8, msg!.Length);
            Assert.Equal(0x7E, msg[0]);
            Assert.Equal(0x03, msg[1]);
            Assert.Equal(0x1F, msg[2]);
            Assert.Equal(0x12, msg[3]);
            Assert.Equal(cmdHi, msg[4]);
            Assert.Equal(0x00, msg[5]);
            Assert.Equal(0x64, msg[6]);
            // Checksum verifies (no 0x7E escape needed for these payloads)
            Assert.Equal(MozaProtocol.CalculateWireChecksum(msg, msg.Length - 1), msg[msg.Length - 1]);
        }

        [Fact]
        public void Mode_BuildWriteMessage_SequentialMatchesCapture()
        {
            // Captured mode-set frame for Sequential (0x09):
            //   7E 03 1F 12 D3 00 09 <chk>
            var cmd = MozaCommandDatabase.Get("ab9-mode");
            Assert.NotNull(cmd);

            byte[]? msg = cmd!.BuildWriteMessage(MozaProtocol.DeviceAb9, new byte[] { 0x09 });
            Assert.NotNull(msg);

            Assert.Equal(8, msg!.Length);
            Assert.Equal(0x7E, msg[0]);
            Assert.Equal(0x03, msg[1]);
            Assert.Equal(0x1F, msg[2]);
            Assert.Equal(0x12, msg[3]);
            Assert.Equal(0xD3, msg[4]);
            Assert.Equal(0x00, msg[5]);
            Assert.Equal(0x09, msg[6]);
            Assert.Equal(MozaProtocol.CalculateWireChecksum(msg, msg.Length - 1), msg[msg.Length - 1]);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(50)]
        [InlineData(99)]
        [InlineData(100)]
        public void Slider_RoundTripValueIsPreserved(int value)
        {
            // Simulated AB9 response: group is request_group | 0x80 on the wire,
            // device id is nibble-swapped. The parser toggles bit7 back to 0x1F
            // and SwapNibbles back to 0x12 before matching the command DB.
            byte responseGroup = 0x1F | 0x80;             // 0x9F
            byte responseDevId = MozaProtocol.SwapNibbles(MozaProtocol.DeviceAb9); // 0x21

            byte[] simulated = new byte[]
            {
                responseGroup, responseDevId,
                0xD6, 0x00,
                (byte)value,
            };

            var parsed = MozaResponseParser.Parse(simulated);
            Assert.NotNull(parsed);
            Assert.Equal("ab9-mech-resistance", parsed!.Value.Name);
            Assert.Equal(value, parsed.Value.IntValue);
            Assert.Equal(MozaProtocol.DeviceAb9, parsed.Value.DeviceId);
        }
    }
}
