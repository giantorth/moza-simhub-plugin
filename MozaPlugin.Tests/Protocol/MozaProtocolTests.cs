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
    }
}
