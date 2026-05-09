using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace MozaPlugin.Telemetry2.Wire
{
    // Per-session chunk buffer. Validates each chunk's CRC32 trailer, strips it,
    // appends the payload to a session-scoped byte stream. Caller drives logical
    // message boundaries (END marker, RPC reply seen, etc.) and asks for the
    // current snapshot.
    //
    // Single-layout CRC: every chunk in /sim/logs/bridge-2026050*.jsonl validates
    // with `crc32(payload[0..-4]) == trailer` (13417 chunks audited, zero exceptions).
    // The 1-byte-prefix layout that the old Telemetry/SessionDataReassembler.cs:78
    // tried as a fallback is not used by any captured wheel firmware. Drop it.
    //
    // Compressed-envelope format (sessions 0x04, 0x09, 0x0B):
    //     [flag:u8 = 0x00] [comp_size:u32 LE] [uncomp_size:u32 LE] [zlib stream]
    // Use TryDecompressEnvelope() once enough bytes are buffered.
    public sealed class ChunkReassembler
    {
        // Cap so a stream that never resolves can't grow unbounded.
        // Real session payloads (configJson, mzdash uploads) sit well below this;
        // overflow means END marker was lost or the stream is corrupted.
        public const int MaxBufferBytes = 1 << 20;

        private readonly List<byte> _buffer = new List<byte>();
        private bool _overflowed;

        public int Length { get { lock (_buffer) return _buffer.Count; } }

        public bool Overflowed { get { lock (_buffer) return _overflowed; } }

        // Append the payload portion of one chunk. The chunk argument is the bytes
        // AFTER the 7c:00:ses:typ:seq header — i.e. payload || crc32-trailer.
        // CRC is verified; on mismatch the chunk is rejected and false is returned.
        public bool AddChunk(byte[] chunk)
        {
            if (chunk == null || chunk.Length < 4) return false;
            if (!TryStripCrc(chunk, out byte[] payload)) return false;
            lock (_buffer)
            {
                if (_buffer.Count + payload.Length > MaxBufferBytes)
                {
                    _overflowed = true;
                    _buffer.Clear();
                    return false;
                }
                _buffer.AddRange(payload);
            }
            return true;
        }

        // Append a chunk by its already-validated payload (no CRC check). For
        // callers that have validated CRC at a different layer.
        public void AddPayload(byte[] payload)
        {
            if (payload == null || payload.Length == 0) return;
            lock (_buffer)
            {
                if (_buffer.Count + payload.Length > MaxBufferBytes)
                {
                    _overflowed = true;
                    _buffer.Clear();
                    return;
                }
                _buffer.AddRange(payload);
            }
        }

        public void Clear()
        {
            lock (_buffer)
            {
                _buffer.Clear();
                _overflowed = false;
            }
        }

        public byte[] Snapshot()
        {
            lock (_buffer) return _buffer.ToArray();
        }

        // Verify CRC and return payload (chunk minus 4-byte trailer). Returns true
        // on success. On mismatch returns false and payload = null. The audit
        // confirms every captured chunk satisfies this layout; no fallback is tried.
        public static bool TryStripCrc(byte[] chunk, out byte[] payload)
        {
            payload = null!;
            if (chunk == null || chunk.Length < 4) return false;
            uint wire = (uint)(chunk[chunk.Length - 4]
                              | (chunk[chunk.Length - 3] << 8)
                              | (chunk[chunk.Length - 2] << 16)
                              | (chunk[chunk.Length - 1] << 24));
            uint actual = Crc32.Compute(chunk, 0, chunk.Length - 4);
            if (actual != wire) return false;
            payload = new byte[chunk.Length - 4];
            Array.Copy(chunk, 0, payload, 0, payload.Length);
            return true;
        }

        // Decode the 9-byte compressed envelope used on sessions 0x04 / 0x09 / 0x0B.
        // Returns the decompressed bytes when buffer holds a complete envelope +
        // matching zlib stream, else null.
        public byte[]? TryDecompressEnvelope()
        {
            byte[] buf = Snapshot();
            if (buf.Length < 9) return null;
            if (buf[0] != 0x00) return null;
            uint compSize = (uint)(buf[1] | (buf[2] << 8) | (buf[3] << 16) | (buf[4] << 24));
            if (buf.Length < 9 + compSize) return null;
            return DecompressZlib(buf, 9);
        }

        // Inflate a zlib stream (CMF + FLG header at `offset`) into bytes.
        // Returns null if the stream can't be inflated.
        public static byte[]? DecompressZlib(byte[] data, int offset)
        {
            if (data == null || offset + 2 > data.Length) return null;
            // Skip 2-byte zlib header (CMF + FLG); .NET DeflateStream takes raw deflate.
            int start = offset + 2;
            int length = data.Length - start;
            if (length <= 0) return null;
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
