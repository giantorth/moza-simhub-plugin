using System;

namespace MozaPlugin.Telemetry2.Wire
{
    // FF property-push record. Wire format observed on h2b session 0x02 in every
    // PitHouse capture (see docs/protocol/findings/2026-05-04-init-sequence.md):
    //   [0xFF] [size:u32LE] [inner_crc32:u32LE] [kind:u32LE] [value:size-4 bytes]
    //
    // size = 4 + value_length (covers kind + value)
    // inner_crc32 = zlib.crc32(kind_bytes_LE || value_bytes), little-endian on wire
    //
    // Header is 13 bytes: 1 (sentinel) + 4 (size) + 4 (crc) + 4 (kind).
    public readonly struct FfRecord
    {
        public uint Kind { get; }
        public byte[] Value { get; }

        public FfRecord(uint kind, byte[] value)
        {
            Kind = kind;
            Value = value ?? Array.Empty<byte>();
        }

        public int WireSize => 1 + 4 + 4 + 4 + Value.Length;

        public byte[] ToBytes()
        {
            // Build kind || value as the CRC payload.
            int valueLen = Value.Length;
            uint size = 4 + (uint)valueLen;
            byte[] crcPayload = new byte[4 + valueLen];
            WriteU32(crcPayload, 0, Kind);
            if (valueLen > 0) Array.Copy(Value, 0, crcPayload, 4, valueLen);
            uint crc = Crc32.Compute(crcPayload);

            byte[] frame = new byte[1 + 4 + 4 + crcPayload.Length];
            frame[0] = 0xFF;
            WriteU32(frame, 1, size);
            WriteU32(frame, 5, crc);
            Array.Copy(crcPayload, 0, frame, 9, crcPayload.Length);
            return frame;
        }

        // Parse one FF record at the given offset. Returns (record, bytesConsumed) or
        // (default, 0) if the bytes don't form a valid record.
        public static (FfRecord rec, int consumed, bool crcOk) ParseAt(byte[] stream, int offset)
        {
            if (offset + 13 > stream.Length || stream[offset] != 0xFF)
                return (default, 0, false);
            uint size = (uint)(stream[offset + 1]
                             | (stream[offset + 2] << 8)
                             | (stream[offset + 3] << 16)
                             | (stream[offset + 4] << 24));
            if (size < 4 || size > 1_000_000) return (default, 0, false);
            int total = 1 + 4 + 4 + (int)size;
            if (offset + total > stream.Length) return (default, 0, false);

            uint declaredCrc = (uint)(stream[offset + 5]
                                    | (stream[offset + 6] << 8)
                                    | (stream[offset + 7] << 16)
                                    | (stream[offset + 8] << 24));
            uint kind = (uint)(stream[offset + 9]
                             | (stream[offset + 10] << 8)
                             | (stream[offset + 11] << 16)
                             | (stream[offset + 12] << 24));
            int valueLen = (int)size - 4;
            byte[] value = new byte[valueLen];
            if (valueLen > 0) Array.Copy(stream, offset + 13, value, 0, valueLen);

            byte[] crcPayload = new byte[4 + valueLen];
            WriteU32(crcPayload, 0, kind);
            if (valueLen > 0) Array.Copy(value, 0, crcPayload, 4, valueLen);
            uint actualCrc = Crc32.Compute(crcPayload);

            return (new FfRecord(kind, value), total, declaredCrc == actualCrc);
        }

        // Typed builders matching the kinds we know about.
        public static FfRecord TimestampInit(uint unixTimeSeconds, int tzOffsetSeconds) =>
            new FfRecord(2, BuildBytes12(unixTimeSeconds, 0u, unchecked((uint)tzOffsetSeconds)));

        public static FfRecord InitCommand() =>
            new FfRecord(7, BuildBytes8(3u, 0u));

        public static FfRecord Heartbeat14(uint value = 100) =>
            new FfRecord(14, BuildBytes4(value));

        public static FfRecord Heartbeat15(uint value) =>
            new FfRecord(15, BuildBytes4(value));

        public static FfRecord DashboardSwitch(uint slotIndex) =>
            new FfRecord(4, BuildBytes8(slotIndex, 0u));

        public static FfRecord BrightnessU32(uint value) =>
            new FfRecord(1, BuildBytes4(value));

        public static FfRecord StandbyU64(ulong msTimeout) =>
            new FfRecord(10, BuildBytes8((uint)(msTimeout & 0xFFFFFFFF), (uint)(msTimeout >> 32)));

        public static FfRecord ChannelCatalog(byte[] zlibPayload) => new FfRecord(8, zlibPayload);
        public static FfRecord ActionCatalog(byte[] zlibPayload) => new FfRecord(11, zlibPayload);

        private static byte[] BuildBytes4(uint a)
        {
            byte[] b = new byte[4];
            WriteU32(b, 0, a);
            return b;
        }

        private static byte[] BuildBytes8(uint a, uint b)
        {
            byte[] o = new byte[8];
            WriteU32(o, 0, a);
            WriteU32(o, 4, b);
            return o;
        }

        private static byte[] BuildBytes12(uint a, uint b, uint c)
        {
            byte[] o = new byte[12];
            WriteU32(o, 0, a);
            WriteU32(o, 4, b);
            WriteU32(o, 8, c);
            return o;
        }

        private static void WriteU32(byte[] dest, int offset, uint value)
        {
            dest[offset] = (byte)(value & 0xFF);
            dest[offset + 1] = (byte)((value >> 8) & 0xFF);
            dest[offset + 2] = (byte)((value >> 16) & 0xFF);
            dest[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
    }
}
