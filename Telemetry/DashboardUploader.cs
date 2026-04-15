using System;
using System.IO;
using System.IO.Compression;
namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Builds the session 0x01 dashboard upload messages.
    ///
    /// PitHouse uploads the .mzdash dashboard file to the wheel on every connection.
    /// The upload uses an FF-prefixed sub-message framing on session 0x01:
    ///
    ///   [FF] [payload_size: u32 LE] [payload]
    ///   [remaining_transfer_size: u32 LE]
    ///   [CRC32: u32 LE]   ← covers all bytes from FF through remaining_size
    ///
    /// Three fields are sent:
    ///   Field 0: 16B correlation tokens [random_u32|0x00000002][timestamp|0x00000000]
    ///            remaining = total bytes of fields 1+2 (dynamic)
    ///   Field 1: 8B protocol constant (identical across VGS and CSP)
    ///            remaining=3 (semantics unknown — not a byte count)
    ///   Field 2: 12B header + zlib-compressed mzdash content (no remaining/CRC)
    ///
    /// Wire format confirmed by CRC-32 verification across 8 sessions (VGS and CSP).
    /// Token structure confirmed from capture analysis: token 1 has random nonce +
    /// constant 0x02 prefix, token 2 is Unix timestamp. Not validated by wheel.
    /// </summary>
    public static class DashboardUploader
    {
        /// <summary>
        /// Build the complete session 0x01 upload message for a dashboard file.
        /// Returns the raw byte stream to be chunked via TierDefinitionBuilder.ChunkMessage().
        /// </summary>
        /// <param name="mzdashContent">Raw .mzdash file content (JSON, UTF-8)</param>
        /// <param name="sessionToken1">Correlation nonce: [random_u32 | 0x00000002] (LE)</param>
        /// <param name="sessionToken2">Session timestamp: [unix_seconds | 0x00000000] (LE)</param>
        public static byte[] BuildUploadMessage(byte[] mzdashContent, ulong sessionToken1, ulong sessionToken2)
        {
            // Compress the mzdash content
            byte[] compressed = CompressZlib(mzdashContent);

            // Pre-zlib 12-byte header: [CRC32: 4B] [08 00 00 00: 4B] [uncompressed_size_BE: 4B]
            uint contentCrc = TierDefinitionBuilder.Crc32(mzdashContent, 0, mzdashContent.Length);
            byte[] preHeader = new byte[12];
            preHeader[0] = (byte)(contentCrc & 0xFF);
            preHeader[1] = (byte)((contentCrc >> 8) & 0xFF);
            preHeader[2] = (byte)((contentCrc >> 16) & 0xFF);
            preHeader[3] = (byte)((contentCrc >> 24) & 0xFF);
            preHeader[4] = 0x08; preHeader[5] = 0x00; preHeader[6] = 0x00; preHeader[7] = 0x00;
            // Uncompressed size in big-endian (confirmed from captures)
            int uSize = mzdashContent.Length;
            preHeader[8]  = (byte)((uSize >> 24) & 0xFF);
            preHeader[9]  = (byte)((uSize >> 16) & 0xFF);
            preHeader[10] = (byte)((uSize >> 8) & 0xFF);
            preHeader[11] = (byte)(uSize & 0xFF);

            // Field 2 payload: pre-header + compressed data
            byte[] field2Payload = new byte[preHeader.Length + compressed.Length];
            Array.Copy(preHeader, 0, field2Payload, 0, preHeader.Length);
            Array.Copy(compressed, 0, field2Payload, preHeader.Length, compressed.Length);

            // Field 0 remaining = total bytes of subsequent fields (field 1 + field 2).
            // Field 1: FF(1) + size(4) + payload(8) + remaining(4) + CRC(4) = 21
            // Field 2: FF(1) + size(4) + field2Payload.Length (no remaining/CRC)
            int field1Block = 5 + 8 + 4 + 4;           // 21
            int field2Block = 5 + field2Payload.Length;
            uint field0Remaining = (uint)(field1Block + field2Block);

            // Field 1 remaining: always 3 in all captures. Semantics unknown —
            // NOT a byte count (field 2 is much larger). Possibly a field count,
            // message type, or protocol constant.
            const uint field1Remaining = 3;

            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            // Field 0: [FF] [10 00 00 00] [16B tokens] [remaining: u32 LE] [CRC32: u32 LE]
            byte[] f0 = BuildField(new byte[16], sessionToken1, sessionToken2, field0Remaining);
            w.Write(f0);

            // Field 1: [FF] [08 00 00 00] [8B constant] [remaining: u32 LE] [CRC32: u32 LE]
            byte[] field1Payload = new byte[] { 0x9E, 0x79, 0x52, 0x7D, 0x07, 0x00, 0x00, 0x00 };
            byte[] f1 = BuildFieldWithRemaining(field1Payload, field1Remaining);
            w.Write(f1);

            // Field 2: [FF] [size: u32 LE] [payload] (no remaining/CRC — last field)
            w.Write((byte)0xFF);
            w.Write((uint)field2Payload.Length);
            w.Write(field2Payload);

            return ms.ToArray();
        }

        private static byte[] BuildField(byte[] placeholder, ulong token1, ulong token2, uint remaining)
        {
            // 16-byte payload: two 8-byte tokens
            byte[] payload = new byte[16];
            BitConverter.GetBytes(token1).CopyTo(payload, 0);
            BitConverter.GetBytes(token2).CopyTo(payload, 8);

            return BuildFieldWithRemaining(payload, remaining);
        }

        private static byte[] BuildFieldWithRemaining(byte[] payload, uint remaining)
        {
            // [FF] [size: u32 LE] [payload] [remaining: u32 LE] [CRC32: u32 LE]
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            w.Write((byte)0xFF);
            w.Write((uint)payload.Length);
            w.Write(payload);
            w.Write(remaining);

            // CRC covers everything from FF through remaining
            byte[] dataForCrc = ms.ToArray();
            uint crc = TierDefinitionBuilder.Crc32(dataForCrc, 0, dataForCrc.Length);
            w.Write(crc);

            return ms.ToArray();
        }

        /// <summary>Zlib-compress data (deflate with zlib header).</summary>
        public static byte[] CompressZlib(byte[] data)
        {
            using var output = new MemoryStream();
            // Write zlib header (78 DA = default compression)
            output.WriteByte(0x78);
            output.WriteByte(0xDA);
            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(data, 0, data.Length);
            }
            // Write Adler-32 checksum (required by zlib format)
            uint adler = Adler32(data);
            output.WriteByte((byte)((adler >> 24) & 0xFF));
            output.WriteByte((byte)((adler >> 16) & 0xFF));
            output.WriteByte((byte)((adler >> 8) & 0xFF));
            output.WriteByte((byte)(adler & 0xFF));
            return output.ToArray();
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

    }
}
