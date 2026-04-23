using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Builds session 0x04 file-transfer sub-messages used to upload a
    /// `.mzdash` dashboard file to the wheel. Replaces the older session 0x01
    /// FF-prefixed 3-field uploader (DashboardUploader) that matched a 2025
    /// firmware snapshot but which the 2025-11 firmware no longer accepts.
    ///
    /// Two sub-messages are sent in sequence:
    ///
    ///   Sub-msg 1 — path registration (no file content):
    ///     header(8)
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
    ///     header(8) with type byte advanced to 0x03
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
    /// Wire format confirmed by decoding usb-capture/09-04-26/dash-upload.pcapng
    /// session 0x04 host→device reassembly (produces a valid .mzdash JSON).
    /// </summary>
    public static class FileTransferBuilder
    {
        /// <summary>Header byte identifying the sender role. Host = 0x02.</summary>
        public const byte HeaderRoleHost = 0x02;
        /// <summary>Header byte identifying the device role (for parsing only).</summary>
        public const byte HeaderRoleDevice = 0x01;
        /// <summary>Max chunk payload size the sender advertises. Host uses 0x40 (64).</summary>
        public const byte HeaderMaxChunkHost = 0x40;
        /// <summary>TLV marker for a local (host-side) path entry. UTF-16LE.</summary>
        public const byte TlvLocalPath = 0x8C;
        /// <summary>TLV marker for a remote (device-side) path entry. UTF-16LE.</summary>
        public const byte TlvRemotePath = 0x84;

        /// <summary>Sentinel indicating no file content in sub-msg 1.</summary>
        private const uint NoContentSentinel = 0xFFFFFFFFu;

        /// <summary>
        /// Build sub-msg 1 — the path-registration preamble. Tells the wheel
        /// the host has a file ready to transfer and declares its MD5 so the
        /// wheel can prepare a staging location.
        /// </summary>
        public static byte[] BuildPathRegistration(string localTempPath,
                                                   string remoteStagingPath,
                                                   byte[] md5,
                                                   uint token)
        {
            if (md5 == null || md5.Length != 16)
                throw new ArgumentException("md5 must be 16 bytes", nameof(md5));
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            WriteHeader(w, 0x01);
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

        /// <summary>
        /// Build sub-msg 2 — the file content push. Contains the destination
        /// path (UTF-16BE) and a zlib-compressed copy of <paramref name="mzdashContent"/>
        /// prefixed by a 12-byte compressed header.
        /// </summary>
        public static byte[] BuildFileContent(string localTempPath,
                                              string remoteStagingPath,
                                              byte[] md5,
                                              uint token,
                                              string destPath,
                                              byte[] mzdashContent)
        {
            if (md5 == null || md5.Length != 16)
                throw new ArgumentException("md5 must be 16 bytes", nameof(md5));
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            WriteHeader(w, 0x03);
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

        private static void WriteHeader(BinaryWriter w, byte transferType)
        {
            w.Write(HeaderRoleHost);      // role
            w.Write(HeaderMaxChunkHost);  // max chunk payload
            w.Write(transferType);        // 0x01 = path registration, 0x03 = content push
            w.Write(new byte[5]);         // reserved
        }

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
