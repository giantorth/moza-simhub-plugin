using System;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Writes values into a byte buffer using LSB-first bit packing.
    /// Algorithm matches Moza Pit House TelemetryBitFormat::assemble (FUN_0080c1b0).
    /// </summary>
    public class TelemetryBitWriter
    {
        private readonly byte[] _buffer;
        private int _bitPosition;

        public TelemetryBitWriter(int byteCount)
        {
            _buffer = new byte[byteCount];
            _bitPosition = 0;
        }

        public int BitPosition => _bitPosition;

        /// <summary>
        /// Write <paramref name="bitCount"/> bits from <paramref name="value"/> (LSB-first).
        /// </summary>
        public void WriteBits(uint value, int bitCount)
        {
            if (bitCount == 0) return;
            int byteOff = _bitPosition / 8;
            int bitOff = _bitPosition % 8;
            if (byteOff + (bitOff + bitCount - 1) / 8 >= _buffer.Length)
                throw new InvalidOperationException(
                    $"TelemetryBitWriter overflow: writing {bitCount} bits at position {_bitPosition} exceeds buffer size {_buffer.Length} bytes");
            int remaining = bitCount;

            while (remaining > 0)
            {
                int take = Math.Min(remaining, 8 - bitOff);
                int mask = ((1 << take) - 1) << bitOff;
                _buffer[byteOff] = (byte)((_buffer[byteOff] & ~mask) | ((int)(value << bitOff) & mask));
                value >>= take;
                byteOff++;
                bitOff = 0;
                remaining -= take;
            }
            _bitPosition += bitCount;
        }

        /// <summary>Write 32-bit IEEE 754 float.</summary>
        public void WriteFloat(float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            uint bits = BitConverter.ToUInt32(bytes, 0);
            WriteBits(bits, 32);
        }

        /// <summary>Write 64-bit IEEE 754 double as two 32-bit halves (little-endian).</summary>
        public void WriteDouble(double value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            uint lo = BitConverter.ToUInt32(bytes, 0);
            uint hi = BitConverter.ToUInt32(bytes, 4);
            WriteBits(lo, 32);
            WriteBits(hi, 32);
        }

        public byte[] GetBuffer() => _buffer;
    }
}
