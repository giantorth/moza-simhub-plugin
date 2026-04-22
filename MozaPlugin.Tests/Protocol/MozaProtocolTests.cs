using MozaPlugin.Protocol;
using Xunit;

namespace MozaPlugin.Tests.Protocol
{
    public class MozaProtocolTests
    {
        [Fact]
        public void CalculateChecksum_EmptyArray_ReturnsMagicValue()
        {
            byte result = MozaProtocol.CalculateChecksum(new byte[0]);
            Assert.Equal(MozaProtocol.MagicValue, result);
        }

        [Fact]
        public void CalculateChecksum_KnownBytes_ReturnsExpected()
        {
            // 0x0D + 0x01 + 0x02 = 0x10
            byte result = MozaProtocol.CalculateChecksum(new byte[] { 0x01, 0x02 });
            Assert.Equal(0x10, result);
        }

        [Fact]
        public void CalculateChecksum_WrapsAt256()
        {
            // 0x0D + 0xFF + 0xFF = 523, mod 256 = 11 = 0x0B
            byte result = MozaProtocol.CalculateChecksum(new byte[] { 0xFF, 0xFF });
            Assert.Equal(0x0B, result);
        }

        [Fact]
        public void CalculateChecksum_WithLength_OnlyHashesPrefix()
        {
            byte[] data = new byte[] { 0x01, 0x02, 0xFF, 0xFF };
            // Only first 2 bytes hashed: 0x0D + 0x01 + 0x02 = 0x10
            Assert.Equal(0x10, MozaProtocol.CalculateChecksum(data, 2));
        }

        [Fact]
        public void CalculateChecksum_CapturedFrame_MatchesLastByte()
        {
            // First frame from usb-capture/12-04-26-2/moza-telemetry-20260412-222643-simhub-test-pattern.txt
            byte[] frame = HexUtil.Parse(
                "7e 18 43 17 7d 23 32 00 23 32 08 20 54 48 d4 c7 17 01 00 00 00 00 40 6a 00 00 00 00 45");
            Assert.Equal(29, frame.Length);
            byte computed = MozaProtocol.CalculateChecksum(frame, frame.Length - 1);
            Assert.Equal(frame[frame.Length - 1], computed);
        }

        [Fact]
        public void CalculateChecksum_CanProduce0x7E()
        {
            // From a real Wireshark capture: 7e 06 3f 17 1a 01 3d 3f 00 00 7e
            // The checksum of this frame is 0x7E, which requires wire-level escaping.
            byte[] frame = HexUtil.Parse("7e 06 3f 17 1a 01 3d 3f 00 00 7e");
            byte computed = MozaProtocol.CalculateChecksum(frame, frame.Length - 1);
            Assert.Equal(MozaProtocol.MessageStart, computed);
            Assert.Equal(frame[frame.Length - 1], computed);
        }

        [Fact]
        public void CalculateChecksum_DeviceFrame_0x7E()
        {
            // Device → host frame with checksum 0x7E (from capture):
            // 7e 07 8e 21 00 00 0b 00 00 00 32 7e
            byte[] frame = HexUtil.Parse("7e 07 8e 21 00 00 0b 00 00 00 32 7e");
            byte computed = MozaProtocol.CalculateChecksum(frame, frame.Length - 1);
            Assert.Equal(MozaProtocol.MessageStart, computed);
        }

        [Theory]
        [InlineData(0x12, 0x21)]
        [InlineData(0xAB, 0xBA)]
        [InlineData(0x00, 0x00)]
        [InlineData(0xFF, 0xFF)]
        [InlineData(0x13, 0x31)]
        public void SwapNibbles_KnownValues(byte input, byte expected)
        {
            Assert.Equal(expected, MozaProtocol.SwapNibbles(input));
        }

        [Theory]
        [InlineData(0x00, 0x80)]
        [InlineData(0x80, 0x00)]
        [InlineData(0x43, 0xC3)]
        [InlineData(0xC3, 0x43)]
        public void ToggleBit7_KnownValues(byte input, byte expected)
        {
            Assert.Equal(expected, MozaProtocol.ToggleBit7(input));
        }

        [Fact]
        public void StuffFrame_NoEscapes_CopiesVerbatim()
        {
            byte[] frame = { 0x7E, 0x03, 0x41, 0x17, 0xAA, 0xBB, 0xCC };
            var dest = new byte[16];
            int len = MozaProtocol.StuffFrame(frame, dest);
            Assert.Equal(frame.Length, len);
            Assert.Equal(frame.Length, MozaProtocol.StuffedFrameSize(frame));
            for (int i = 0; i < frame.Length; i++)
                Assert.Equal(frame[i], dest[i]);
        }

        [Fact]
        public void StuffFrame_PayloadHas7E_DoublesIt()
        {
            byte[] frame = { 0x7E, 0x04, 0x41, 0x17, 0x7E, 0xBB, 0xCC };
            var dest = new byte[16];
            int len = MozaProtocol.StuffFrame(frame, dest);
            Assert.Equal(frame.Length + 1, len);
            Assert.Equal(frame.Length + 1, MozaProtocol.StuffedFrameSize(frame));
            byte[] expected = { 0x7E, 0x04, 0x41, 0x17, 0x7E, 0x7E, 0xBB, 0xCC };
            for (int i = 0; i < expected.Length; i++)
                Assert.Equal(expected[i], dest[i]);
        }

        [Fact]
        public void StuffFrame_HeaderLenBytePreserved_NotStuffed()
        {
            // 0x7E in header position 1 (length byte) must not be doubled — only
            // bytes from index 2 onward are stuffed. This edge case matters for
            // frames with payload-length 126 (0x7E) which are legal.
            byte[] frame = new byte[0x7E + 3];
            frame[0] = 0x7E;
            frame[1] = 0x7E;
            frame[2] = 0x41;
            frame[3] = 0x17;
            var dest = new byte[frame.Length * 2];
            int len = MozaProtocol.StuffFrame(frame, dest);
            Assert.Equal(frame.Length, len);
            Assert.Equal(0x7E, dest[0]);
            Assert.Equal(0x7E, dest[1]);
            Assert.Equal(0x41, dest[2]);
        }

        [Fact]
        public void StuffFrame_MultipleEscapes()
        {
            byte[] frame = { 0x7E, 0x05, 0x43, 0x17, 0x7E, 0x00, 0x7E, 0x7E };
            var dest = new byte[32];
            int len = MozaProtocol.StuffFrame(frame, dest);
            // Three 0x7E in the stuffable region (indices 4, 6, 7) → +3 escapes.
            Assert.Equal(frame.Length + 3, len);
            byte[] expected = { 0x7E, 0x05, 0x43, 0x17, 0x7E, 0x7E, 0x00, 0x7E, 0x7E, 0x7E, 0x7E };
            for (int i = 0; i < expected.Length; i++)
                Assert.Equal(expected[i], dest[i]);
        }
    }
}
