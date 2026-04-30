using Xunit;
using SessionPropertyPushBuilder = global::MozaPlugin.Protocol.SessionPropertyPushBuilder;
using TierDefinitionBuilder = global::MozaPlugin.Telemetry.TierDefinitionBuilder;

namespace MozaPlugin.Tests.Protocol
{
    public class SessionPropertyPushBuilderTests
    {
        // Captured 50% brightness frame from live CSP wheel sim
        // (docs/protocol/findings/2026-04-29-session-01-property-push.md):
        //   7e 1b 43 17 7c 00 01 01 46 01 ff 08 00 00 00 dd ef aa f3
        //   01 00 00 00 32 00 00 00 2c 6e 11 53 66
        // After SOF/LEN/group/dev (4 bytes) and chunk hdr "7c 00 01 01 46 01"
        // (6 bytes of payload), the net data = 17 bytes:
        //   ff 08 00 00 00 dd ef aa f3 01 00 00 00 32 00 00 00
        [Fact]
        public void BuildU32Body_Brightness50_MatchesCapturedWireBytes()
        {
            byte[] body = SessionPropertyPushBuilder.BuildU32Body(
                kind: SessionPropertyPushBuilder.KindDashBrightness,
                value: 50);

            byte[] expected =
            {
                0xff, 0x08, 0x00, 0x00, 0x00,           // ff + size=8
                0xdd, 0xef, 0xaa, 0xf3,                 // inner CRC32 LE
                0x01, 0x00, 0x00, 0x00,                 // kind=1 LE
                0x32, 0x00, 0x00, 0x00                  // value=50 LE
            };
            Assert.Equal(expected, body);
        }

        [Fact]
        public void BuildU32Body_Brightness0_MatchesCapturedHash()
        {
            byte[] body = SessionPropertyPushBuilder.BuildU32Body(
                SessionPropertyPushBuilder.KindDashBrightness, 0);
            // Inner CRC for kind=1 value=0 = f7 df 88 a9 LE
            Assert.Equal(0xf7, body[5]);
            Assert.Equal(0xdf, body[6]);
            Assert.Equal(0x88, body[7]);
            Assert.Equal(0xa9, body[8]);
        }

        [Fact]
        public void BuildU64Body_Standby25Min_MatchesCapturedWireBytes()
        {
            // 25 min = 1,500,000 ms. Captured inner CRC: b9 d3 ce a6 LE.
            ulong ms25min = 25UL * 60_000UL; // 1,500,000
            byte[] body = SessionPropertyPushBuilder.BuildU64Body(
                kind: SessionPropertyPushBuilder.KindDashStandbyMs,
                value: ms25min);

            byte[] expected =
            {
                0xff, 0x0c, 0x00, 0x00, 0x00,                       // ff + size=12
                0xb9, 0xd3, 0xce, 0xa6,                             // inner CRC32 LE
                0x0a, 0x00, 0x00, 0x00,                             // kind=10 LE
                0x60, 0xe3, 0x16, 0x00, 0x00, 0x00, 0x00, 0x00      // value=1500000 LE u64
            };
            Assert.Equal(expected, body);
        }

        [Fact]
        public void BuildU64Body_Standby3Min_MatchesCapturedHash()
        {
            // 3 min = 180,000 ms. Captured inner CRC: 1a d2 61 f3 LE.
            byte[] body = SessionPropertyPushBuilder.BuildU64Body(
                SessionPropertyPushBuilder.KindDashStandbyMs, 180_000UL);
            Assert.Equal(0x1a, body[5]);
            Assert.Equal(0xd2, body[6]);
            Assert.Equal(0x61, body[7]);
            Assert.Equal(0xf3, body[8]);
        }

        [Fact]
        public void BuildU32Body_WrappedByChunkMessage_MatchesFullCapturedFrame()
        {
            // Full captured 50% brightness frame (seq 0x0146):
            byte[] expected =
            {
                0x7e, 0x1b, 0x43, 0x17,
                0x7c, 0x00, 0x01, 0x01, 0x46, 0x01,
                0xff, 0x08, 0x00, 0x00, 0x00,
                0xdd, 0xef, 0xaa, 0xf3,
                0x01, 0x00, 0x00, 0x00,
                0x32, 0x00, 0x00, 0x00,
                0x2c, 0x6e, 0x11, 0x53,
                0x66
            };

            byte[] body = SessionPropertyPushBuilder.BuildU32Body(
                SessionPropertyPushBuilder.KindDashBrightness, 50);

            int seq = 0x0146;
            var frames = TierDefinitionBuilder.ChunkMessage(body, session: 0x01, seq: ref seq);

            Assert.Single(frames);
            Assert.Equal(expected, frames[0]);
            Assert.Equal(0x0147, seq);
        }
    }
}
