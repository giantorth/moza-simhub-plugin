using MozaPlugin.Protocol;
using Xunit;

namespace MozaPlugin.Tests.Protocol
{
    public class MozaCommandTests
    {
        [Fact]
        public void BuildReadMessage_BaseLimit_CorrectFormat()
        {
            var cmd = MozaCommandDatabase.Commands["base-limit"];
            byte[]? msg = cmd.BuildReadMessage(MozaProtocol.DeviceBase);

            // base-limit: ReadGroup=40 (0x28), CommandId=[1], PayloadBytes=2
            // Read payload is zero-filled (matches boxflat's prepare_message): [0x00, 0x00]
            // Frame: 7E 03 28 13 01 00 00 [checksum]
            // Checksum: (0x0D + 0x7E + 0x03 + 0x28 + 0x13 + 0x01 + 0x00 + 0x00) % 256 = 0xCA
            byte[] expected = { 0x7E, 0x03, 0x28, 0x13, 0x01, 0x00, 0x00, 0xCA };
            Assert.Equal(expected, msg);
        }

        [Fact]
        public void BuildReadMessage_NotReadable_ReturnsNull()
        {
            // Find a command with ReadGroup == 0xFF (no command in DB? Check write-only)
            // From database, base-state has ReadGroup=43, WriteGroup=0xFF — that's write-not-readable.
            // Let me find a read-only check. Actually, looking at base-state (read=43, write=0xFF) is read-only.
            // For BuildReadMessage to return null we need ReadGroup == 0xFF.
            // wheel-send-rpm-telemetry from the DB: ReadGroup=0xFF.
            var cmd = MozaCommandDatabase.Commands["wheel-send-rpm-telemetry"];
            Assert.Equal(0xFF, cmd.ReadGroup);
            byte[]? msg = cmd.BuildReadMessage(MozaProtocol.DeviceWheel);
            Assert.Null(msg);
        }

        [Fact]
        public void BuildWriteMessage_NotWritable_ReturnsNull()
        {
            // base-state: ReadGroup=43, WriteGroup=0xFF (read-only)
            var cmd = MozaCommandDatabase.Commands["base-state"];
            Assert.Equal(0xFF, cmd.WriteGroup);
            byte[]? msg = cmd.BuildWriteMessage(MozaProtocol.DeviceBase, new byte[] { 0x00, 0x00 });
            Assert.Null(msg);
        }

        [Fact]
        public void BuildWriteInt_BaseLimit_BigEndianPayload()
        {
            var cmd = MozaCommandDatabase.Commands["base-limit"];
            byte[]? msg = cmd.BuildWriteInt(MozaProtocol.DeviceBase, 500);

            // payload = [0x01, 0xF4] (BE 500)
            // Frame: 7E 03 29 13 01 01 F4 [checksum]
            // Checksum: (0x0D + 0x7E + 0x03 + 0x29 + 0x13 + 0x01 + 0x01 + 0xF4) % 256 = 0xC0
            byte[] expected = { 0x7E, 0x03, 0x29, 0x13, 0x01, 0x01, 0xF4, 0xC0 };
            Assert.Equal(expected, msg);
        }

        [Fact]
        public void BuildWriteFloat_BigEndianIEEE754()
        {
            var cmd = MozaCommandDatabase.Commands["base-limit"];
            byte[]? msg = cmd.BuildWriteFloat(MozaProtocol.DeviceBase, 1.5f);

            // 1.5f IEEE 754 = 0x3FC00000, BE = 3F C0 00 00
            // Frame: 7E 05 29 13 01 3F C0 00 00 [checksum]
            Assert.NotNull(msg);
            Assert.Equal(0x7E, msg![0]);
            Assert.Equal(0x05, msg[1]);  // payloadLength = 1 (cmdId) + 4 (float)
            Assert.Equal(0x29, msg[2]);  // WriteGroup
            Assert.Equal(0x13, msg[3]);  // DeviceBase
            Assert.Equal(0x01, msg[4]);  // CommandId
            Assert.Equal(0x3F, msg[5]);
            Assert.Equal(0xC0, msg[6]);
            Assert.Equal(0x00, msg[7]);
            Assert.Equal(0x00, msg[8]);
            // Checksum verifies
            Assert.Equal(MozaProtocol.CalculateChecksum(msg, msg.Length - 1), msg[msg.Length - 1]);
        }

        [Fact]
        public void ParseIntValue_BigEndian2Bytes()
        {
            int value = MozaCommand.ParseIntValue(new byte[] { 0x01, 0xF4 }, 2);
            Assert.Equal(500, value);
        }

        [Fact]
        public void ParseIntValue_SingleByte()
        {
            Assert.Equal(0x42, MozaCommand.ParseIntValue(new byte[] { 0x42 }, 1));
        }

        [Fact]
        public void ParseIntValue_TooShort_ReturnsZero()
        {
            Assert.Equal(0, MozaCommand.ParseIntValue(new byte[] { 0x42 }, 4));
        }

        [Fact]
        public void ParseFloatValue_BigEndian()
        {
            // 1.5f BE = 3F C0 00 00
            float value = MozaCommand.ParseFloatValue(new byte[] { 0x3F, 0xC0, 0x00, 0x00 });
            Assert.Equal(1.5f, value);
        }

        [Fact]
        public void ParseFloatValue_TooShort_ReturnsZero()
        {
            Assert.Equal(0f, MozaCommand.ParseFloatValue(new byte[] { 0x3F }));
        }

        [Fact]
        public void BuildWriteInt_ChecksumValid_AllCommands()
        {
            // Sanity check: every int-type write produces a frame whose last byte is a valid checksum.
            foreach (var kvp in MozaCommandDatabase.Commands)
            {
                var cmd = kvp.Value;
                if (cmd.WriteGroup == 0xFF) continue;
                if (cmd.PayloadType != "int") continue;

                byte[]? msg = cmd.BuildWriteInt(0x17, 1);
                Assert.NotNull(msg);
                byte expected = MozaProtocol.CalculateChecksum(msg!, msg!.Length - 1);
                Assert.Equal(expected, msg[msg.Length - 1]);
            }
        }
    }
}
