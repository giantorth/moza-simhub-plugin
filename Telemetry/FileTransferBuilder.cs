using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Wire-format variant of the file-transfer sub-msg header.
    /// </summary>
    /// <remarks>
    /// Two formats observed across firmware revisions. <see cref="Legacy2025_11"/>
    /// is the original 8-byte `[role:1][max_chunk:1][type:1][reserved:5]` form
    /// captured in `usb-capture/09-04-26/dash-upload.pcapng`. <see cref="New2026_04"/>
    /// is a 6-byte `[type:1][size_LE:2][pad:3]` header where size_LE is the
    /// sub-msg body length — observed in 2026-04 PitHouse uploads (sessions
    /// 0x07 / 0x09). Bytes on the wire ARE NOT byte-identical between the two:
    /// the type byte position differs (byte 0 in new vs byte 2 in legacy), and
    /// the size_LE field's two bytes overlap legacy's `max_chunk` + `type`
    /// positions, so a legacy-encoded sub-msg 2 (content) would be misread as
    /// new-format size 0x0340 = 832 bytes. New firmware needs the new layout.
    /// </remarks>
    public enum FileTransferWireFormat
    {
        /// <summary>2025-11 firmware: 8-byte role/max_chunk/type/5×reserved header.</summary>
        Legacy2025_11 = 0,
        /// <summary>2026-04 firmware: 6-byte type/size_LE/3×reserved header.</summary>
        New2026_04 = 1,
    }

    /// <summary>
    /// Builds file-transfer sub-messages used to upload a `.mzdash` dashboard
    /// file to the wheel. Replaces the older session 0x01 FF-prefixed 3-field
    /// uploader that matched a pre-2025 firmware snapshot but which 2025-11+
    /// firmware no longer accepts.
    ///
    /// Two sub-messages are sent in sequence:
    ///
    ///   Sub-msg 1 — path registration (no file content):
    ///     header(8 legacy / 6 new)
    ///     0x8C local_path_utf16le (null-terminated)
    ///     0x8C local_path_utf16le (repeat)
    ///     0x84 remote_staging_path_utf16le
    ///     0x84 remote_staging_path_utf16le (repeat)
    ///     MD5_len(1=0x10) + MD5(16)
    ///     reserved(4=0x00000000)
    ///     token(4 LE)
    ///     sentinel(4=0xFFFFFFFF)
    ///
    ///   Sub-msg 2 — file content push:
    ///     header(8 legacy with type=0x03 / 6 new with type=0x03)
    ///     0x8C local_path_utf16le + repeat
    ///     0x84 remote_staging_path_utf16le + repeat
    ///     MD5_len(1=0x10) + MD5(16)
    ///     reserved(4)
    ///     token(4 LE) + token(4 LE)
    ///     file_count(4 LE = 1)
    ///     dest_path_byte_len(4 LE)
    ///     dest_path_utf16BE (NOT null-terminated)
    ///     compressed_header(12) + zlib_stream
    ///
    /// The 12-byte compressed header before the zlib stream:
    ///   CRC32(uncompressed, LE=4B) + 0x08 0x00 0x00 0x00 (4B) + uncompressed_size_BE(4B)
    ///
    /// Legacy wire format confirmed by decoding usb-capture/09-04-26/dash-upload.pcapng
    /// session 0x04 host→device reassembly. New format documented in
    /// <c>docs/moza-protocol.md</c> §§ "6-byte sub-msg header", "Per-chunk
    /// metadata trailer".
    /// </summary>
    public static class FileTransferBuilder
    {
        /// <summary>Header byte identifying the sender role. Host = 0x02. Legacy format only.</summary>
        public const byte HeaderRoleHost = 0x02;
        /// <summary>Header byte identifying the device role (for parsing only). Legacy format only.</summary>
        public const byte HeaderRoleDevice = 0x01;
        /// <summary>Max chunk payload size the sender advertises. Host uses 0x40 (64). Legacy format only.</summary>
        public const byte HeaderMaxChunkHost = 0x40;
        /// <summary>TLV marker for a local (host-side) path entry. UTF-16LE.</summary>
        public const byte TlvLocalPath = 0x8C;
        /// <summary>TLV marker for a remote (device-side) path entry. UTF-16LE. Legacy format only — new firmware uses 0x70.</summary>
        public const byte TlvRemotePath = 0x84;

        /// <summary>Sentinel indicating no file content in sub-msg 1.</summary>
        private const uint NoContentSentinel = 0xFFFFFFFFu;

        /// <summary>
        /// Build sub-msg 1 — the path-registration preamble. Tells the wheel
        /// the host has a file ready to transfer and declares its MD5 so the
        /// wheel can prepare a staging location. Legacy 2025-11 wire format.
        /// </summary>
        public static byte[] BuildPathRegistration(string localTempPath,
                                                   string remoteStagingPath,
                                                   byte[] md5,
                                                   uint token)
            => BuildPathRegistration(localTempPath, remoteStagingPath, md5, token,
                FileTransferWireFormat.Legacy2025_11);

        /// <summary>
        /// Build sub-msg 1 with the chosen wire format.
        /// </summary>
        public static byte[] BuildPathRegistration(string localTempPath,
                                                   string remoteStagingPath,
                                                   byte[] md5,
                                                   uint token,
                                                   FileTransferWireFormat format)
        {
            if (md5 == null || md5.Length != 16)
                throw new ArgumentException("md5 must be 16 bytes", nameof(md5));
            byte[] body = BuildPathRegistrationBody(localTempPath, remoteStagingPath, md5, token);
            return Compose(0x01, body, format);
        }

        /// <summary>
        /// Build sub-msg 2 — the file content push. Contains the destination
        /// path (UTF-16BE) and a zlib-compressed copy of <paramref name="mzdashContent"/>
        /// prefixed by a 12-byte compressed header. Legacy 2025-11 wire format.
        /// </summary>
        public static byte[] BuildFileContent(string localTempPath,
                                              string remoteStagingPath,
                                              byte[] md5,
                                              uint token,
                                              string destPath,
                                              byte[] mzdashContent)
            => BuildFileContent(localTempPath, remoteStagingPath, md5, token, destPath,
                mzdashContent, FileTransferWireFormat.Legacy2025_11);

        /// <summary>
        /// Build sub-msg 2 with the chosen wire format.
        /// </summary>
        public static byte[] BuildFileContent(string localTempPath,
                                              string remoteStagingPath,
                                              byte[] md5,
                                              uint token,
                                              string destPath,
                                              byte[] mzdashContent,
                                              FileTransferWireFormat format)
        {
            if (md5 == null || md5.Length != 16)
                throw new ArgumentException("md5 must be 16 bytes", nameof(md5));
            byte[] body = BuildFileContentBody(localTempPath, remoteStagingPath, md5, token,
                destPath, mzdashContent);
            return Compose(0x03, body, format);
        }

        /// <summary>
        /// Build sub-msg 2 as one or more sub-msgs (file content push), splitting if
        /// the body exceeds the 16-bit size cap of <see cref="FileTransferWireFormat.New2026_04"/>.
        /// </summary>
        /// <remarks>
        /// MVP scope: returns a single-element list for both wire formats when the
        /// total body fits in <c>0xFFFF</c> bytes, which covers typical mzdash files
        /// (≤ ~50 KB compressed). Larger uploads need true multi-sub-msg splitting
        /// per <c>docs/moza-protocol.md § "Per-chunk metadata trailer (continuation
        /// chunks)"</c> — the per-chunk counter semantics at body[281..283] and the
        /// 7B constant at body[284..290] still need a clean PitHouse capture to
        /// reverse-engineer. Until then, oversized uploads will throw at
        /// <see cref="BuildSubMsgHeader"/>.
        /// </remarks>
        public static System.Collections.Generic.List<byte[]> BuildFileContentChunked(
            string localTempPath, string remoteStagingPath, byte[] md5, uint token,
            string destPath, byte[] mzdashContent, FileTransferWireFormat format)
        {
            // For now both formats produce a single sub-msg. Hook left as a List
            // so the call sites (DashboardUploader, TelemetrySender) don't need
            // to change shape when true chunking lands.
            byte[] singleSubMsg = BuildFileContent(localTempPath, remoteStagingPath, md5, token,
                destPath, mzdashContent, format);
            return new System.Collections.Generic.List<byte[]> { singleSubMsg };
        }

        // ── body builders (header-agnostic) ──────────────────────────────────

        private static byte[] BuildPathRegistrationBody(string localTempPath,
                                                        string remoteStagingPath,
                                                        byte[] md5,
                                                        uint token)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            WritePathTlv(w, TlvLocalPath, localTempPath);
            WritePathTlv(w, TlvLocalPath, localTempPath);
            WritePathTlv(w, TlvRemotePath, remoteStagingPath);
            WritePathTlv(w, TlvRemotePath, remoteStagingPath);
            w.Write((byte)0x10);              // MD5 length
            w.Write(md5);
            w.Write((uint)0);                 // reserved
            w.Write(token);
            w.Write(NoContentSentinel);       // signals "no content in this sub-msg"
            return ms.ToArray();
        }

        private static byte[] BuildFileContentBody(string localTempPath,
                                                   string remoteStagingPath,
                                                   byte[] md5,
                                                   uint token,
                                                   string destPath,
                                                   byte[] mzdashContent)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            WritePathTlv(w, TlvLocalPath, localTempPath);
            WritePathTlv(w, TlvLocalPath, localTempPath);
            WritePathTlv(w, TlvRemotePath, remoteStagingPath);
            WritePathTlv(w, TlvRemotePath, remoteStagingPath);
            w.Write((byte)0x10);              // MD5 length
            w.Write(md5);
            w.Write((uint)0);                 // reserved
            w.Write(token);                   // token #1
            w.Write(token);                   // token #2 (same value observed)
            w.Write((uint)1);                 // file_count = 1
            byte[] destBytes = Encoding.BigEndianUnicode.GetBytes(destPath);
            w.Write((uint)destBytes.Length);
            w.Write(destBytes);
            w.Write(BuildCompressedHeader(mzdashContent));
            w.Write(CompressZlib(mzdashContent));
            return ms.ToArray();
        }

        private static byte[] Compose(byte transferType, byte[] body, FileTransferWireFormat format)
        {
            byte[] header = BuildSubMsgHeader(transferType, body.Length, format);
            byte[] result = new byte[header.Length + body.Length];
            Buffer.BlockCopy(header, 0, result, 0, header.Length);
            Buffer.BlockCopy(body, 0, result, header.Length, body.Length);
            return result;
        }

        /// <summary>
        /// Build the file-transfer sub-msg header for the chosen wire format.
        /// </summary>
        /// <remarks>
        /// Legacy: <c>[role=0x02][max_chunk=0x40][type][reserved×5]</c> = 8 bytes,
        /// no explicit body-size field (size is implicit from the session-data
        /// chunking layer + sentinels).
        /// New: <c>[type][size_LE_2B][pad×3]</c> = 6 bytes, where size_LE is the
        /// body byte count. Body size capped at 65535 (firmware splits larger
        /// uploads across multiple sub-msgs each &lt;= 4384 bytes; see
        /// docs/moza-protocol.md § "Per-chunk metadata trailer").
        /// </remarks>
        public static byte[] BuildSubMsgHeader(byte transferType, int bodyLength,
            FileTransferWireFormat format)
        {
            if (bodyLength < 0) throw new ArgumentOutOfRangeException(nameof(bodyLength));
            if (format == FileTransferWireFormat.Legacy2025_11)
                return new byte[] { HeaderRoleHost, HeaderMaxChunkHost, transferType, 0, 0, 0, 0, 0 };
            if (format == FileTransferWireFormat.New2026_04)
            {
                if (bodyLength > 0xFFFF)
                    throw new ArgumentOutOfRangeException(nameof(bodyLength),
                        "New2026_04 sub-msg body must fit in 16 bits; split into multiple sub-msgs.");
                return new byte[]
                {
                    transferType,
                    (byte)(bodyLength & 0xFF),
                    (byte)((bodyLength >> 8) & 0xFF),
                    0, 0, 0,
                };
            }
            throw new ArgumentException($"Unknown wire format: {format}", nameof(format));
        }

        /// <summary>
        /// 12-byte pre-zlib header: CRC32(uncompressed, LE) + `08 00 00 00` + uncompressed_size (BE).
        /// </summary>
        public static byte[] BuildCompressedHeader(byte[] uncompressed)
        {
            uint crc = TierDefinitionBuilder.Crc32(uncompressed, 0, uncompressed.Length);
            var hdr = new byte[12];
            hdr[0] = (byte)(crc & 0xFF);
            hdr[1] = (byte)((crc >> 8) & 0xFF);
            hdr[2] = (byte)((crc >> 16) & 0xFF);
            hdr[3] = (byte)((crc >> 24) & 0xFF);
            hdr[4] = 0x08; hdr[5] = 0x00; hdr[6] = 0x00; hdr[7] = 0x00;
            uint uLen = (uint)uncompressed.Length;
            hdr[8] = (byte)((uLen >> 24) & 0xFF);
            hdr[9] = (byte)((uLen >> 16) & 0xFF);
            hdr[10] = (byte)((uLen >> 8) & 0xFF);
            hdr[11] = (byte)(uLen & 0xFF);
            return hdr;
        }

        /// <summary>zlib-compress (deflate with 78 DA header + Adler-32 trailer).</summary>
        public static byte[] CompressZlib(byte[] data)
        {
            using var output = new MemoryStream();
            output.WriteByte(0x78);
            output.WriteByte(0xDA);
            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
                deflate.Write(data, 0, data.Length);
            uint adler = Adler32(data);
            output.WriteByte((byte)((adler >> 24) & 0xFF));
            output.WriteByte((byte)((adler >> 16) & 0xFF));
            output.WriteByte((byte)((adler >> 8) & 0xFF));
            output.WriteByte((byte)(adler & 0xFF));
            return output.ToArray();
        }

        /// <summary>MD5 of the raw mzdash bytes. Used for path naming + integrity.</summary>
        public static byte[] ComputeMd5(byte[] content)
        {
            using var md5 = MD5.Create();
            return md5.ComputeHash(content);
        }

        public static string Md5Hex(byte[] md5)
        {
            var sb = new StringBuilder(md5.Length * 2);
            foreach (var b in md5) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>
        /// Build a local temp-file path the same way PitHouse names them.
        /// Used only as a label in the TLV preamble — the file never actually
        /// needs to exist on disk for the transfer to succeed.
        /// </summary>
        public static string BuildLocalTempPath(long timestampMs)
        {
            string baseDir = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "Temp");
            return $"{baseDir.Replace('\\', '/')}/_moza_filetransfer_tmp_{timestampMs}";
        }

        /// <summary>Device staging path the wheel uses for in-flight files.</summary>
        public static string BuildRemoteStagingPath(string md5Hex)
            => $"/home/root/_moza_filetransfer_md5_{md5Hex}";

        /// <summary>Final destination path under the wheel's dashboard resource tree.</summary>
        public static string BuildDashboardDestPath(string dashboardName)
            => $"/home/moza/resource/dashes/{dashboardName}/{dashboardName}.mzdash";

        private static void WritePathTlv(BinaryWriter w, byte marker, string path)
        {
            w.Write(marker);
            w.Write((byte)0);
            byte[] bytes = Encoding.Unicode.GetBytes(path);
            w.Write(bytes);
            w.Write((byte)0);  // UTF-16LE null terminator
            w.Write((byte)0);
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
