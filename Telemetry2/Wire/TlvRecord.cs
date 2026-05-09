using System;
using System.Collections.Generic;
using System.IO;

namespace MozaPlugin.Telemetry2.Wire
{
    // Tier-def TLV record. Wire format observed in every PitHouse capture:
    //   [tag:u8] [size:u32LE] [value:size bytes]
    // Tags used (per docs/protocol/findings/2026-05-04-tierdef-reference.md):
    //   0x00 ENABLE_PREV_TIER (size=1, value=flag byte)
    //   0x01 TIER             (size=1+16N, value=[flag:u8][N × 16-byte channel records])
    //   0x03 FLAG_BASE        (size=0, terminator marking end of preamble)
    //   0x06 END_MARKER       (size=4, value=u32LE max-channel-idx watermark)
    //   0x07 PROTO_VER        (size=4, value=u32LE protocol version, =2 in observed traffic)
    public readonly struct TlvRecord
    {
        public byte Tag { get; }
        public byte[] Value { get; }

        public TlvRecord(byte tag, byte[] value)
        {
            Tag = tag;
            Value = value ?? Array.Empty<byte>();
        }

        public int WireSize => 1 + 4 + Value.Length;

        public void WriteTo(BinaryWriter w)
        {
            w.Write(Tag);
            w.Write((uint)Value.Length);
            if (Value.Length > 0) w.Write(Value);
        }

        public byte[] ToBytes()
        {
            using var ms = new MemoryStream(WireSize);
            using var w = new BinaryWriter(ms);
            WriteTo(w);
            return ms.ToArray();
        }

        // Common-case helpers used by the builder.
        public static TlvRecord ProtoVersion(uint version) =>
            new TlvRecord(0x07, BitConverter.GetBytes(version));

        public static TlvRecord FlagBase() =>
            new TlvRecord(0x03, Array.Empty<byte>());

        public static TlvRecord EnablePrev(byte flag) =>
            new TlvRecord(0x00, new[] { flag });

        public static TlvRecord EndMarker(uint maxChannelIdx) =>
            new TlvRecord(0x06, BitConverter.GetBytes(maxChannelIdx));

        public static TlvRecord Tier(byte flag, ChannelRecord[] channels)
        {
            byte[] value = new byte[1 + channels.Length * ChannelRecord.WireSize];
            value[0] = flag;
            for (int i = 0; i < channels.Length; i++)
                channels[i].WriteAt(value, 1 + i * ChannelRecord.WireSize);
            return new TlvRecord(0x01, value);
        }

        // Parse a TLV stream. Stops cleanly at end-of-data; if a record's declared size
        // would overrun, returns the records parsed so far (no throw). Unknown tags are
        // returned as opaque records — caller decides what to do with them.
        public static IList<TlvRecord> ParseStream(byte[] stream, int offset = 0, int length = -1)
        {
            if (length < 0) length = stream.Length - offset;
            var records = new List<TlvRecord>();
            int pos = offset;
            int end = offset + length;
            while (pos + 5 <= end)
            {
                byte tag = stream[pos];
                uint size = (uint)(stream[pos + 1]
                                 | (stream[pos + 2] << 8)
                                 | (stream[pos + 3] << 16)
                                 | (stream[pos + 4] << 24));
                if (size > (uint)(end - pos - 5)) break;
                byte[] value = new byte[size];
                if (size > 0) Array.Copy(stream, pos + 5, value, 0, (int)size);
                records.Add(new TlvRecord(tag, value));
                pos += 5 + (int)size;
            }
            return records;
        }
    }

    // Per-channel record in a TIER (tag 0x01) value. Wire format:
    //   [idx:u32LE] [comp:u32LE] [bit_width:u32LE] [reserved:u32LE = 0]
    // Size = 16 bytes. Confirmed across all bridge captures.
    public readonly struct ChannelRecord
    {
        public const int WireSize = 16;

        public uint Index { get; }
        public uint Compression { get; }
        public uint BitWidth { get; }

        public ChannelRecord(uint index, uint compression, uint bitWidth)
        {
            Index = index;
            Compression = compression;
            BitWidth = bitWidth;
        }

        public void WriteAt(byte[] dest, int offset)
        {
            WriteU32(dest, offset, Index);
            WriteU32(dest, offset + 4, Compression);
            WriteU32(dest, offset + 8, BitWidth);
            WriteU32(dest, offset + 12, 0u);
        }

        private static void WriteU32(byte[] dest, int offset, uint value)
        {
            dest[offset] = (byte)(value & 0xFF);
            dest[offset + 1] = (byte)((value >> 8) & 0xFF);
            dest[offset + 2] = (byte)((value >> 16) & 0xFF);
            dest[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        public static ChannelRecord ParseAt(byte[] src, int offset)
        {
            uint idx = (uint)(src[offset] | (src[offset + 1] << 8) | (src[offset + 2] << 16) | (src[offset + 3] << 24));
            uint comp = (uint)(src[offset + 4] | (src[offset + 5] << 8) | (src[offset + 6] << 16) | (src[offset + 7] << 24));
            uint bw = (uint)(src[offset + 8] | (src[offset + 9] << 8) | (src[offset + 10] << 16) | (src[offset + 11] << 24));
            return new ChannelRecord(idx, comp, bw);
        }
    }
}
