using System.Text;
using MozaPlugin.Protocol;
using MozaPlugin.Telemetry;
using MozaPlugin.Tests.Integration;
using Xunit;

namespace MozaPlugin.Tests.Telemetry
{
    public class TierDefinitionBuilderTests
    {
        [Fact]
        public void Crc32_StandardTestVector()
        {
            // CRC-32 of ASCII "123456789" = 0xCBF43926
            byte[] data = Encoding.ASCII.GetBytes("123456789");
            Assert.Equal(0xCBF43926u, TierDefinitionBuilder.Crc32(data, 0, data.Length));
        }

        [Fact]
        public void Crc32_EmptyRange_IsZero()
        {
            // CRC-32 of empty input = 0
            Assert.Equal(0u, TierDefinitionBuilder.Crc32(new byte[] { 0x01, 0x02 }, 0, 0));
        }

        [Fact]
        public void ChunkMessage_ShortMessage_SingleChunk()
        {
            byte[] msg = new byte[20];
            for (int i = 0; i < msg.Length; i++) msg[i] = (byte)i;

            int seq = 0;
            var frames = TierDefinitionBuilder.ChunkMessage(msg, 0x02, ref seq);

            Assert.Single(frames);
            Assert.Equal(1, seq);

            var f = frames[0];
            Assert.Equal(0x7E, f[0]);
            Assert.Equal(0x43, f[2]);   // group
            Assert.Equal(0x17, f[3]);   // device
            Assert.Equal(0x7C, f[4]);
            Assert.Equal(0x00, f[5]);
            Assert.Equal(0x02, f[6]);   // session
            Assert.Equal(0x01, f[7]);   // type=data
            Assert.Equal(0x00, f[8]);   // seq lo
            Assert.Equal(0x00, f[9]);   // seq hi

            // N = cmd(2)+session(1)+type(1)+seq(2)+payload(20+4 crc) = 30
            Assert.Equal((byte)30, f[1]);

            // Checksum valid
            Assert.Equal(MozaProtocol.CalculateChecksum(f, f.Length - 1), f[f.Length - 1]);

            // Payload body at offset 10..29 matches msg bytes
            for (int i = 0; i < 20; i++)
                Assert.Equal(msg[i], f[10 + i]);

            // Trailing CRC matches CRC over msg
            uint crc = TierDefinitionBuilder.Crc32(msg, 0, msg.Length);
            Assert.Equal((byte)(crc & 0xFF),         f[30]);
            Assert.Equal((byte)((crc >> 8) & 0xFF),  f[31]);
            Assert.Equal((byte)((crc >> 16) & 0xFF), f[32]);
            Assert.Equal((byte)((crc >> 24) & 0xFF), f[33]);
        }

        [Fact]
        public void ChunkMessage_LongMessage_MultipleChunks()
        {
            // 120 bytes > 54 → 3 chunks (54 + 54 + 12)
            byte[] msg = new byte[120];
            for (int i = 0; i < msg.Length; i++) msg[i] = (byte)i;

            int seq = 5;
            var frames = TierDefinitionBuilder.ChunkMessage(msg, 0x02, ref seq);

            Assert.Equal(3, frames.Count);
            Assert.Equal(8, seq);

            // Sequence numbers increment
            Assert.Equal(0x05, frames[0][8]);
            Assert.Equal(0x06, frames[1][8]);
            Assert.Equal(0x07, frames[2][8]);

            // Every chunk has a valid frame checksum
            foreach (var f in frames)
                Assert.Equal(MozaProtocol.CalculateChecksum(f, f.Length - 1), f[f.Length - 1]);
        }

        [Fact]
        public void BuildTierDefinitionMessage_F1Profile_StructureValid()
        {
            var profile = F1DashboardProfileFixture.BuildMultiStream();
            byte[] msg = TierDefinitionBuilder.BuildTierDefinitionMessage(profile, flagBase: 0x00);

            int numChannels = profile.Tiers[0].Channels.Count;

            // PitHouse shape with no prior flags (flagBase=0):
            //   tier_def (6 + 16*N) + end_marker (9) = 15 + 16*N
            int expectedSize = (6 + 16 * numChannels) + 9;
            Assert.Equal(expectedSize, msg.Length);

            Assert.Equal(0x01, msg[0]);
            uint size = (uint)(msg[1] | (msg[2] << 8) | (msg[3] << 16) | (msg[4] << 24));
            Assert.Equal((uint)(1 + numChannels * 16), size);
            Assert.Equal(0x00, msg[5]);

            int endOffset = msg.Length - 9;
            Assert.Equal(0x06, msg[endOffset]);
            Assert.Equal(0x04, msg[endOffset + 1]);
            uint markerVal = (uint)(msg[endOffset + 5] | (msg[endOffset + 6] << 8)
                                  | (msg[endOffset + 7] << 16) | (msg[endOffset + 8] << 24));
            // END val = max channel idx in this msg
            Assert.Equal((uint)numChannels, markerVal);
        }

        [Fact]
        public void BuildTierDefinitionMessage_ChannelIndices_AreOneBasedAlphabetical()
        {
            var profile = F1DashboardProfileFixture.BuildMultiStream();
            byte[] msg = TierDefinitionBuilder.BuildTierDefinitionMessage(profile, flagBase: 0x00);

            // First channel entry starts at offset 6 (1 tag + 4 size + 1 flag).
            int ch0 = 6;
            uint idx0 = (uint)(msg[ch0] | (msg[ch0 + 1] << 8) | (msg[ch0 + 2] << 16) | (msg[ch0 + 3] << 24));
            Assert.Equal(1u, idx0);

            int ch1 = ch0 + 16;
            uint idx1 = (uint)(msg[ch1] | (msg[ch1 + 1] << 8) | (msg[ch1 + 2] << 16) | (msg[ch1 + 3] << 24));
            Assert.Equal(2u, idx1);
        }

        [Fact]
        public void BuildTierDefinitionV2_PriorFlags_EmitsEnablesInHeader()
        {
            var profile = F1DashboardProfileFixture.BuildMultiStream();
            byte[] msg = TierDefinitionBuilder.BuildTierDefinitionV2(
                profile, flagBase: 0x05, wheelCatalog: null);

            // 5 ENABLE records each [tag=0x00][size=01000000][flag] = 6B.
            for (int i = 0; i < 5; i++)
            {
                int o = i * 6;
                Assert.Equal(0x00, msg[o]);
                Assert.Equal(0x01, msg[o + 1]);
                Assert.Equal(i,    msg[o + 5]);
            }
            // Tier-def starts after enables.
            Assert.Equal(0x01, msg[30]);
            Assert.Equal(0x05, msg[35]); // flag = flagBase
        }

        [Fact]
        public void BuildV0UrlSubscription_StructureValid()
        {
            var profile = F1DashboardProfileFixture.BuildMultiStream();
            byte[] msg = TierDefinitionBuilder.BuildV0UrlSubscription(profile);

            // Starts with 0xFF sentinel
            Assert.Equal(0xFF, msg[0]);

            // Config tag 0x03, param_size=4, value=1
            Assert.Equal(0x03, msg[1]);
            Assert.Equal(0x04, msg[2]);  // size LE
            Assert.Equal(0x01, msg[6]);  // value = 1 (LE)
        }
    }
}
