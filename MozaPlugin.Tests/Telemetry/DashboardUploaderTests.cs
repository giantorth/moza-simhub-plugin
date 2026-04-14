using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using MozaPlugin.Telemetry;
using Xunit;

namespace MozaPlugin.Tests.Telemetry
{
    public class DashboardUploaderTests
    {
        [Fact]
        public void CompressZlib_HasZlibHeader()
        {
            byte[] data = Encoding.ASCII.GetBytes("hello world");
            byte[] compressed = DashboardUploader.CompressZlib(data);

            Assert.Equal(0x78, compressed[0]);
            Assert.Equal(0xDA, compressed[1]);
        }

        [Fact]
        public void CompressZlib_RoundTrip_ViaDeflateStream()
        {
            byte[] original = Encoding.UTF8.GetBytes(
                "{\"name\":\"F1 Dashboard\",\"children\":[{\"type\":\"label\",\"text\":\"RPM\"}]}");
            byte[] compressed = DashboardUploader.CompressZlib(original);

            // Strip 2-byte zlib header and 4-byte adler trailer to get raw deflate
            byte[] rawDeflate = new byte[compressed.Length - 6];
            Array.Copy(compressed, 2, rawDeflate, 0, rawDeflate.Length);

            using var input = new MemoryStream(rawDeflate);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            deflate.CopyTo(output);

            Assert.Equal(original, output.ToArray());
        }

        [Fact]
        public void CompressZlib_Adler32Trailer_Correct()
        {
            // Known Adler-32 test vector: Adler32("Wikipedia") = 0x11E60398
            byte[] data = Encoding.ASCII.GetBytes("Wikipedia");
            byte[] compressed = DashboardUploader.CompressZlib(data);

            // Trailer is last 4 bytes, big-endian
            int n = compressed.Length;
            uint adler = (uint)(compressed[n - 4] << 24)
                       | (uint)(compressed[n - 3] << 16)
                       | (uint)(compressed[n - 2] << 8)
                       | compressed[n - 1];
            Assert.Equal(0x11E60398u, adler);
        }

        [Fact]
        public void BuildUploadMessage_Field0_StructureValid()
        {
            byte[] mzdash = Encoding.UTF8.GetBytes("{\"test\":\"dash\"}");
            ulong token1 = 0x1122334455667788UL;
            ulong token2 = 0xAABBCCDDEEFF0011UL;

            byte[] msg = DashboardUploader.BuildUploadMessage(mzdash, token1, token2);

            // Field 0 starts at offset 0: FF 10 00 00 00 [16 tokens] [4 remaining] [4 crc]
            Assert.Equal(0xFF, msg[0]);
            Assert.Equal(0x10, msg[1]);  // size=16 LE
            Assert.Equal(0x00, msg[2]);
            Assert.Equal(0x00, msg[3]);
            Assert.Equal(0x00, msg[4]);

            // Token 1 at offset 5..12 (little-endian)
            Assert.Equal(0x88, msg[5]);
            Assert.Equal(0x77, msg[6]);
            Assert.Equal(0x11, msg[12]);

            // Token 2 at offset 13..20 (little-endian)
            Assert.Equal(0x11, msg[13]);
            Assert.Equal(0x00, msg[14]);
            Assert.Equal(0xAA, msg[20]);

            // CRC-32 of field 0 covers bytes [0..24] (FF..remaining)
            uint expectedCrc = TierDefinitionBuilder.Crc32(msg, 0, 25);
            uint actualCrc = (uint)msg[25]
                           | ((uint)msg[26] << 8)
                           | ((uint)msg[27] << 16)
                           | ((uint)msg[28] << 24);
            Assert.Equal(expectedCrc, actualCrc);
        }

        [Fact]
        public void BuildUploadMessage_Field1_HasConstantPayloadAndValidCrc()
        {
            byte[] mzdash = Encoding.UTF8.GetBytes("{}");
            byte[] msg = DashboardUploader.BuildUploadMessage(mzdash, 0, 0);

            // Field 1 starts at offset 29 (after field 0 = 29 bytes)
            int f1 = 29;
            Assert.Equal(0xFF, msg[f1 + 0]);
            Assert.Equal(0x08, msg[f1 + 1]);  // size=8 LE
            Assert.Equal(0x00, msg[f1 + 2]);
            Assert.Equal(0x00, msg[f1 + 3]);
            Assert.Equal(0x00, msg[f1 + 4]);

            // Constant payload bytes
            byte[] expected = { 0x9E, 0x79, 0x52, 0x7D, 0x07, 0x00, 0x00, 0x00 };
            for (int i = 0; i < 8; i++)
                Assert.Equal(expected[i], msg[f1 + 5 + i]);

            // CRC covers bytes [f1..f1+16] (FF through remaining)
            uint expectedCrc = TierDefinitionBuilder.Crc32(msg, f1, 17);
            uint actualCrc = (uint)msg[f1 + 17]
                           | ((uint)msg[f1 + 18] << 8)
                           | ((uint)msg[f1 + 19] << 16)
                           | ((uint)msg[f1 + 20] << 24);
            Assert.Equal(expectedCrc, actualCrc);
        }

        [Fact]
        public void BuildUploadMessage_Field2_PreHeaderAndZlibPayload()
        {
            byte[] mzdash = Encoding.UTF8.GetBytes("{\"name\":\"testing\"}");
            byte[] msg = DashboardUploader.BuildUploadMessage(mzdash, 0, 0);

            // Field 2 starts at offset 29 + 21 = 50
            int f2 = 50;
            Assert.Equal(0xFF, msg[f2]);

            // Size (LE) at offset f2+1..f2+4
            uint payloadSize = (uint)msg[f2 + 1]
                             | ((uint)msg[f2 + 2] << 8)
                             | ((uint)msg[f2 + 3] << 16)
                             | ((uint)msg[f2 + 4] << 24);
            Assert.Equal((uint)(msg.Length - (f2 + 5)), payloadSize);

            // Pre-header starts at f2+5
            int ph = f2 + 5;

            // [0..3] = CRC-32 of mzdash content (LE)
            uint expectedContentCrc = TierDefinitionBuilder.Crc32(mzdash, 0, mzdash.Length);
            uint actualContentCrc = (uint)msg[ph]
                                  | ((uint)msg[ph + 1] << 8)
                                  | ((uint)msg[ph + 2] << 16)
                                  | ((uint)msg[ph + 3] << 24);
            Assert.Equal(expectedContentCrc, actualContentCrc);

            // [4..7] = 08 00 00 00
            Assert.Equal(0x08, msg[ph + 4]);
            Assert.Equal(0x00, msg[ph + 5]);
            Assert.Equal(0x00, msg[ph + 6]);
            Assert.Equal(0x00, msg[ph + 7]);

            // [8..11] = uncompressed size in BIG-endian
            int uSize = mzdash.Length;
            Assert.Equal((byte)((uSize >> 24) & 0xFF), msg[ph + 8]);
            Assert.Equal((byte)((uSize >> 16) & 0xFF), msg[ph + 9]);
            Assert.Equal((byte)((uSize >> 8) & 0xFF),  msg[ph + 10]);
            Assert.Equal((byte)(uSize & 0xFF),         msg[ph + 11]);

            // Zlib payload begins at ph+12
            int zStart = ph + 12;
            Assert.Equal(0x78, msg[zStart]);
            Assert.Equal(0xDA, msg[zStart + 1]);
        }

        [Fact]
        public void BuildUploadMessage_Field2_ZlibRoundTripMatchesInput()
        {
            byte[] mzdash = Encoding.UTF8.GetBytes(
                "{\"name\":\"roundtrip\",\"values\":[1,2,3,4,5],\"nested\":{\"k\":\"v\"}}");
            byte[] msg = DashboardUploader.BuildUploadMessage(mzdash, 0xDEADBEEFUL, 0xCAFEBABEUL);

            // Find field 2 zlib payload
            int f2 = 50;
            int zStart = f2 + 5 + 12;
            int zLen = msg.Length - zStart;

            // Strip zlib header (2) and adler trailer (4) → raw deflate
            byte[] rawDeflate = new byte[zLen - 6];
            Array.Copy(msg, zStart + 2, rawDeflate, 0, rawDeflate.Length);

            using var input = new MemoryStream(rawDeflate);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            deflate.CopyTo(output);

            Assert.Equal(mzdash, output.ToArray());
        }

        [Fact]
        public void BuildUploadMessage_TotalLength_MatchesFieldSizes()
        {
            byte[] mzdash = Encoding.UTF8.GetBytes("dashboard content");
            byte[] msg = DashboardUploader.BuildUploadMessage(mzdash, 0, 0);

            // Field 0 = 29, Field 1 = 21, Field 2 header = 5, Field 2 payload = 12+compressed
            // Min total = 29 + 21 + 5 + 12 + 2 (zlib hdr) + 4 (adler) = 73 plus deflate bytes
            Assert.True(msg.Length >= 73);

            // Field 2 size field should match remainder
            int f2 = 50;
            uint payloadSize = (uint)msg[f2 + 1]
                             | ((uint)msg[f2 + 2] << 8)
                             | ((uint)msg[f2 + 3] << 16)
                             | ((uint)msg[f2 + 4] << 24);
            Assert.Equal((uint)(msg.Length - f2 - 5), payloadSize);
        }
    }
}
