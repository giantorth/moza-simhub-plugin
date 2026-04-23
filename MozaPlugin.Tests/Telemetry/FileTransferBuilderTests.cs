using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using MozaPlugin.Telemetry;
using Xunit;

namespace MozaPlugin.Tests.Telemetry
{
    public class FileTransferBuilderTests
    {
        [Fact]
        public void BuildCompressedHeader_HasCrcAndConstantAndSizeBE()
        {
            byte[] content = Encoding.UTF8.GetBytes("hello");
            byte[] header = FileTransferBuilder.BuildCompressedHeader(content);
            Assert.Equal(12, header.Length);
            // Constant middle 4 bytes
            Assert.Equal(0x08, header[4]);
            Assert.Equal(0x00, header[5]);
            Assert.Equal(0x00, header[6]);
            Assert.Equal(0x00, header[7]);
            // Size stored big-endian
            uint sizeBE = (uint)(header[8] << 24 | header[9] << 16 | header[10] << 8 | header[11]);
            Assert.Equal((uint)content.Length, sizeBE);
            // CRC32 stored little-endian
            uint crcLE = (uint)(header[0] | header[1] << 8 | header[2] << 16 | header[3] << 24);
            Assert.Equal(TierDefinitionBuilder.Crc32(content, 0, content.Length), crcLE);
        }

        [Fact]
        public void CompressZlib_HasZlibHeaderAndRoundTrips()
        {
            byte[] content = Encoding.UTF8.GetBytes("{\"name\":\"demo\"}");
            byte[] compressed = FileTransferBuilder.CompressZlib(content);
            Assert.Equal(0x78, compressed[0]);
            Assert.Equal(0xDA, compressed[1]);
            byte[] raw = new byte[compressed.Length - 6]; // drop zlib hdr + adler
            Array.Copy(compressed, 2, raw, 0, raw.Length);
            using var ms = new MemoryStream(raw);
            using var def = new DeflateStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            def.CopyTo(outMs);
            Assert.Equal(content, outMs.ToArray());
        }

        [Fact]
        public void BuildPathRegistration_StartsWithCorrectHeader()
        {
            byte[] md5 = new byte[16];
            byte[] msg = FileTransferBuilder.BuildPathRegistration(
                "C:/tmp/local", "/home/root/staging", md5, token: 0x054Bu);
            Assert.Equal(FileTransferBuilder.HeaderRoleHost, msg[0]);
            Assert.Equal(FileTransferBuilder.HeaderMaxChunkHost, msg[1]);
            Assert.Equal(0x01, msg[2]); // transfer type for sub-msg 1
            Assert.Equal(0x8C, msg[8]); // first TLV marker = local path
        }

        [Fact]
        public void BuildFileContent_EmbedsDestPathAsUtf16BE()
        {
            byte[] md5 = new byte[16];
            byte[] mzdash = Encoding.UTF8.GetBytes("dummy");
            byte[] msg = FileTransferBuilder.BuildFileContent(
                "local", "remote", md5, 0x054Bu,
                "/home/moza/resource/dashes/X/X.mzdash", mzdash);
            // Must contain UTF-16BE-encoded destination path
            byte[] needle = Encoding.BigEndianUnicode.GetBytes("/home/moza/resource/dashes/X/X.mzdash");
            Assert.NotEmpty(IndexesOf(msg, needle));
        }

        [Fact]
        public void ComputeMd5_KnownVector()
        {
            // md5("hello") = 5d41402abc4b2a76b9719d911017c592
            byte[] hello = Encoding.ASCII.GetBytes("hello");
            byte[] md5 = FileTransferBuilder.ComputeMd5(hello);
            string hex = FileTransferBuilder.Md5Hex(md5);
            Assert.Equal("5d41402abc4b2a76b9719d911017c592", hex);
        }

        [Fact]
        public void BuildDashboardDestPath_MatchesCapturedFormat()
        {
            string p = FileTransferBuilder.BuildDashboardDestPath("Rally V1");
            Assert.Equal("/home/moza/resource/dashes/Rally V1/Rally V1.mzdash", p);
        }

        private static System.Collections.Generic.IEnumerable<int> IndexesOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                }
                if (match) yield return i;
            }
        }
    }
}
