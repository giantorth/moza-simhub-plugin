using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Reassembles 7c:00 type=0x01 session data chunks into the underlying
    /// application message. Real-wheel chunks have a 4-byte CRC32 trailer
    /// (verified by <see cref="TierDefinitionBuilder.Crc32"/>). Some chunks
    /// carry an extra 1-byte flag prefix before the TLV payload (observed in
    /// pithouse-style tier def chunks); we try both strip layouts and pick
    /// whichever makes the CRC match.
    ///
    /// Once enough bytes are buffered the caller can decode the 9-byte
    /// compressed envelope used on sessions 0x04 and 0x09:
    ///
    ///   [flag:1B=0x00] [comp_size:u32 LE] [uncomp_size:u32 LE] [zlib stream]
    ///
    /// The zlib stream uses deflate with a zlib header (78 9c / 78 da). The
    /// reassembler doesn't know how many compressed bytes to expect up front
    /// — the envelope size field is only present on the FIRST chunk of a
    /// message — so callers should invoke <see cref="TryDecompress"/> when a
    /// logical message boundary is signalled elsewhere (e.g. session 0x04 END
    /// marker, session 0x09 seq increment stall).
    /// </summary>
    public sealed class SessionDataReassembler
    {
        private readonly List<byte> _buffer = new();

        public int Length { get { lock (_buffer) return _buffer.Count; } }

        /// <summary>Feed one raw chunk payload (the bytes after session/type/seq).</summary>
        public void AddChunk(byte[] chunk)
        {
            if (chunk == null || chunk.Length < 4) return;
            byte[] net = StripCrcTrailer(chunk);
            lock (_buffer)
                _buffer.AddRange(net);
        }

        public void Clear() { lock (_buffer) _buffer.Clear(); }

        /// <summary>Snapshot the current reassembled buffer.</summary>
        public byte[] Snapshot()
        {
            lock (_buffer)
                return _buffer.ToArray();
        }

        /// <summary>
        /// Strip the per-chunk CRC32 trailer. Two layouts are tried: (a) the
        /// full chunk is the CRC'd payload; (b) the first byte is a flag prefix
        /// and CRC covers bytes[1..-4]. Whichever matches wins. If neither
        /// matches we conservatively strip the last 4 bytes.
        /// </summary>
        public static byte[] StripCrcTrailer(byte[] chunk)
        {
            if (chunk.Length < 4) return chunk;
            uint wire = (uint)(chunk[chunk.Length - 4]
                              | (chunk[chunk.Length - 3] << 8)
                              | (chunk[chunk.Length - 2] << 16)
                              | (chunk[chunk.Length - 1] << 24));
            uint crcFull = TierDefinitionBuilder.Crc32(chunk, 0, chunk.Length - 4);
            if (crcFull == wire)
            {
                var o = new byte[chunk.Length - 4];
                Array.Copy(chunk, 0, o, 0, o.Length);
                return o;
            }
            if (chunk.Length >= 5)
            {
                uint crcStrip1 = TierDefinitionBuilder.Crc32(chunk, 1, chunk.Length - 5);
                if (crcStrip1 == wire)
                {
                    var o = new byte[chunk.Length - 5];
                    Array.Copy(chunk, 1, o, 0, o.Length);
                    return o;
                }
            }
            var fallback = new byte[chunk.Length - 4];
            Array.Copy(chunk, 0, fallback, 0, fallback.Length);
            return fallback;
        }

        /// <summary>
        /// Decode the 9-byte compressed envelope + zlib payload. Returns
        /// decompressed bytes or null if the buffer doesn't look like an
        /// envelope yet (header too short, zlib decompression failed, etc).
        /// Caller is responsible for deciding when to call this — typically
        /// after seeing a session END marker or a complete RPC round-trip.
        /// </summary>
        public byte[]? TryDecompress()
        {
            byte[] buf = Snapshot();
            if (buf.Length < 9) return null;
            if (buf[0] == 0x00)
            {
                uint compSize = (uint)(buf[1] | (buf[2] << 8) | (buf[3] << 16) | (buf[4] << 24));
                if (buf.Length >= 9 + compSize)
                {
                    byte[]? r = DecompressZlib(buf, 9);
                    if (r != null) return r;
                }
            }
            // Fallback: mzdash bodies embed raw 0x7E bytes that interact with
            // the wire-level escape rules (see docs/moza-protocol.md:31-54).
            // If envelope-offset decode failed, scan for zlib magic (78 9c / 78 da)
            // and try each candidate — mirrors sim/wheel_sim.py:1742-1770.
            return TryDecompressByMagic(buf);
        }

        /// <summary>
        /// Scan <paramref name="buf"/> for zlib magic bytes (78 9c / 78 da) and
        /// trial-decompress each hit. Returns the first stream that inflates
        /// without error. Used as a fallback when envelope-offset decoding
        /// fails because embedded 0x7E in mzdash JSON corrupted the header.
        /// </summary>
        public static byte[]? TryDecompressByMagic(byte[] buf)
        {
            for (int i = 0; i + 2 <= buf.Length; i++)
            {
                if (buf[i] != 0x78) continue;
                if (buf[i + 1] != 0x9c && buf[i + 1] != 0xda) continue;
                byte[]? r = DecompressZlib(buf, i);
                if (r != null && r.Length > 0) return r;
            }
            return null;
        }

        /// <summary>
        /// Inflate a zlib stream (`78 9c` / `78 da`) starting at the given
        /// offset. Returns decompressed bytes or null on corruption.
        /// </summary>
        public static byte[]? DecompressZlib(byte[] data, int offset)
        {
            if (offset + 2 > data.Length) return null;
            // Skip 2-byte zlib header (CMF + FLG). DeflateStream expects raw deflate.
            int start = offset + 2;
            int length = data.Length - start;
            // Last 4 bytes of a full zlib stream are the Adler-32 checksum —
            // we can't know where the stream ends exactly without trial
            // decompression. Strip 4 bytes if we can't decode with them.
            using var ms = new MemoryStream(data, start, length);
            using var def = new DeflateStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            try
            {
                def.CopyTo(outMs);
            }
            catch (InvalidDataException)
            {
                return null;
            }
            return outMs.ToArray();
        }
    }
}
