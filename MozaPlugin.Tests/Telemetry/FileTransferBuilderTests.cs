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
            // Default = Legacy2025_11
            Assert.Equal(FileTransferBuilder.HeaderRoleHost, msg[0]);
            Assert.Equal(FileTransferBuilder.HeaderMaxChunkHost, msg[1]);
            Assert.Equal(0x01, msg[2]); // transfer type for sub-msg 1
            Assert.Equal(0x8C, msg[8]); // first TLV marker = local path
        }

        [Fact]
        public void BuildPathRegistration_New2026_04_StartsWithTypeSizeHeader()
        {
            byte[] md5 = new byte[16];
            byte[] msg = FileTransferBuilder.BuildPathRegistration(
                "C:/tmp/local", "/home/root/staging", md5, token: 0x054Bu,
                FileTransferWireFormat.New2026_04);
            // 6B header: type / size_LE_2B / pad×3
            Assert.Equal(0x01, msg[0]);   // transfer type
            int sizeLE = msg[1] | (msg[2] << 8);
            Assert.Equal(msg.Length - 6, sizeLE); // size_LE = body length
            Assert.Equal(0x00, msg[3]);   // pad
            Assert.Equal(0x00, msg[4]);
            Assert.Equal(0x00, msg[5]);
            Assert.Equal(0x8C, msg[6]);   // body[0] = LOCAL TLV marker
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
        public void BuildFileContent_New2026_04_HeaderHasTypeAndSize()
        {
            byte[] md5 = new byte[16];
            byte[] mzdash = Encoding.UTF8.GetBytes("dummy");
            byte[] msg = FileTransferBuilder.BuildFileContent(
                "local", "remote", md5, 0x054Bu,
                "/home/moza/resource/dashes/X/X.mzdash", mzdash,
                FileTransferWireFormat.New2026_04);
            Assert.Equal(0x03, msg[0]); // transfer type for content push
            int sizeLE = msg[1] | (msg[2] << 8);
            Assert.Equal(msg.Length - 6, sizeLE);
        }

        [Fact]
        public void BuildPathRegistration_New2026_04_Type02_UsesType02WithPadAndXor()
        {
            byte[] md5 = new byte[16];
            for (int i = 0; i < 16; i++) md5[i] = (byte)(0x10 + i);
            byte[] msg = FileTransferBuilder.BuildPathRegistration(
                "C:/tmp/local", "ignored-for-metadata", md5, token: 0x054Bu,
                FileTransferWireFormat.New2026_04_Type02);

            // 6B header
            Assert.Equal(0x02, msg[0]); // type=0x02 METADATA
            int sizeLE = msg[1] | (msg[2] << 8);
            Assert.Equal(msg.Length - 6, sizeLE);

            // Body[0..1] = 2-byte 00 00 pad
            Assert.Equal(0x00, msg[6]);
            Assert.Equal(0x00, msg[7]);

            // Body[2] = LOCAL TLV marker
            Assert.Equal(0x8C, msg[8]);

            // No REMOTE TLV in metadata sub-msg.
            byte[] localUtf16 = Encoding.Unicode.GetBytes("C:/tmp/local");
            Assert.NotEmpty(IndexesOf(msg, localUtf16));

            // Last byte = XOR over body bytes excluding itself.
            byte computed = 0;
            for (int i = 6; i < msg.Length - 1; i++) computed ^= msg[i];
            Assert.Equal(computed, msg[msg.Length - 1]);
        }

        [Fact]
        public void BuildFileContent_New2026_04_Type02_UsesNewRemoteTlvAndXor()
        {
            byte[] md5 = new byte[16];
            byte[] mzdash = Encoding.UTF8.GetBytes("dummy mzdash content");
            byte[] msg = FileTransferBuilder.BuildFileContent(
                "C:/tmp/loc", "/_moza_filetransfer_md5_abc", md5, 0x0u,
                "/home/moza/resource/dashes/X/X.mzdash", mzdash,
                FileTransferWireFormat.New2026_04_Type02);

            Assert.Equal(0x03, msg[0]); // CONTENT
            // Body 2B pad
            Assert.Equal(0x00, msg[6]);
            Assert.Equal(0x00, msg[7]);
            // LOCAL TLV first
            Assert.Equal(0x8C, msg[8]);
            // REMOTE TLV uses NEW marker 0x70 — search for it in body
            byte[] remoteUtf16 = Encoding.Unicode.GetBytes("/_moza_filetransfer_md5_abc");
            int remoteOff = -1;
            foreach (var idx in IndexesOf(msg, remoteUtf16)) { remoteOff = idx; break; }
            Assert.True(remoteOff > 0, "remote path not found in body");
            Assert.Equal(0x70, msg[remoteOff - 2]); // marker precedes utf-16le bytes by 2

            // Last byte = XOR over body
            byte computed = 0;
            for (int i = 6; i < msg.Length - 1; i++) computed ^= msg[i];
            Assert.Equal(computed, msg[msg.Length - 1]);
        }

        [Fact]
        public void BuildCompressedHeaderType02_LayoutIsULenBE_CLenBE_CrcLE()
        {
            byte[] uncompressed = Encoding.UTF8.GetBytes("hello world");
            int compressedLen = 19;
            byte[] hdr = FileTransferBuilder.BuildCompressedHeaderType02(uncompressed, compressedLen);
            Assert.Equal(12, hdr.Length);
            uint uLen = (uint)(hdr[0] << 24 | hdr[1] << 16 | hdr[2] << 8 | hdr[3]);
            Assert.Equal((uint)uncompressed.Length, uLen);
            uint cLen = (uint)(hdr[4] << 24 | hdr[5] << 16 | hdr[6] << 8 | hdr[7]);
            Assert.Equal((uint)compressedLen, cLen);
            uint crc = (uint)(hdr[8] | hdr[9] << 8 | hdr[10] << 16 | hdr[11] << 24);
            Assert.Equal(TierDefinitionBuilder.Crc32(uncompressed, 0, uncompressed.Length), crc);
        }

        [Fact]
        public void BuildV0ValueFrame_KnownVector_PithouseCsp()
        {
            // From PitHouse capture wireshark/csp/start-game-change-dash.pcapng
            // host outbound on session 0x02:
            //   ff 08 00 00 00 0f ad ec c4 0e 00 00 00 64 00 00 00
            // (channel index 14, value 100, CRC32 over index_LE||value_LE)
            byte[] valueLE = { 0x64, 0x00, 0x00, 0x00 }; // 100 LE
            byte[] frame = TelemetryFrameBuilder.BuildV0ValueFrame(
                channelIndex: 14, valueLE);
            byte[] expected = {
                0xFF, 0x08, 0x00, 0x00, 0x00,
                0x0F, 0xAD, 0xEC, 0xC4,
                0x0E, 0x00, 0x00, 0x00,
                0x64, 0x00, 0x00, 0x00,
            };
            Assert.Equal(expected, frame);
        }

        [Fact]
        public void BuildV0ValueFrame_8ByteValue_KnownVector()
        {
            // From PitHouse capture host out:
            //   ff 0c 00 00 00 a5 e6 36 ab 04 00 00 00 08 00 00 00 00 00 00 00
            byte[] valueLE = { 0x08, 0, 0, 0, 0, 0, 0, 0 };
            byte[] frame = TelemetryFrameBuilder.BuildV0ValueFrame(
                channelIndex: 4, valueLE);
            byte[] expected = {
                0xFF, 0x0C, 0x00, 0x00, 0x00,
                0xA5, 0xE6, 0x36, 0xAB,
                0x04, 0x00, 0x00, 0x00,
                0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            };
            Assert.Equal(expected, frame);
        }

        [Fact]
        public void BuildV0ValueFrame_RejectsBadValueLength()
        {
            Assert.Throws<ArgumentException>(() =>
                TelemetryFrameBuilder.BuildV0ValueFrame(0, new byte[2]));
            Assert.Throws<ArgumentException>(() =>
                TelemetryFrameBuilder.BuildV0ValueFrame(0, new byte[16]));
        }

        [Fact]
        public void BuildSubMsgHeader_New2026_04_RejectsOversizedBody()
        {
            // 16-bit size cap: body > 0xFFFF must split into multiple sub-msgs.
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                FileTransferBuilder.BuildSubMsgHeader(0x03, 0x10000,
                    FileTransferWireFormat.New2026_04));
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
