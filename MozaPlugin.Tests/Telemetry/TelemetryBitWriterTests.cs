using System;
using MozaPlugin.Telemetry;
using Xunit;

namespace MozaPlugin.Tests.Telemetry
{
    public class TelemetryBitWriterTests
    {
        [Fact]
        public void WriteBits_SingleBit_LSBFirst()
        {
            var w = new TelemetryBitWriter(1);
            w.WriteBits(1u, 1);
            Assert.Equal(0x01, w.GetBuffer()[0]);
            Assert.Equal(1, w.BitPosition);
        }

        [Fact]
        public void WriteBits_8Bits_FillsByte()
        {
            var w = new TelemetryBitWriter(1);
            w.WriteBits(0xAB, 8);
            Assert.Equal(0xAB, w.GetBuffer()[0]);
        }

        [Fact]
        public void WriteBits_10Bits_SpansTwoBytes_LSBFirst()
        {
            var w = new TelemetryBitWriter(2);
            // 0x3E8 = 1000 = 0b11_1110_1000
            // LSB-first: byte0 = 0xE8, byte1 = 0b00000011 = 0x03
            w.WriteBits(0x3E8, 10);
            Assert.Equal(0xE8, w.GetBuffer()[0]);
            Assert.Equal(0x03, w.GetBuffer()[1]);
        }

        [Fact]
        public void WriteBits_16Bits_LittleEndian()
        {
            var w = new TelemetryBitWriter(2);
            w.WriteBits(0x1234, 16);
            Assert.Equal(0x34, w.GetBuffer()[0]);
            Assert.Equal(0x12, w.GetBuffer()[1]);
        }

        [Fact]
        public void WriteBits_AcrossByteBoundaryAtOddOffset()
        {
            var w = new TelemetryBitWriter(2);
            w.WriteBits(0x05, 4);    // 4 bits at offset 0: byte0 low nibble = 0x05
            w.WriteBits(0xAB, 8);    // 8 bits at offset 4: spans bytes 0-1
            // After: byte0 = 0xB5 (low nibble 0x5 from first write, high nibble 0xB from low half of 0xAB)
            // byte1 = 0x0A (high half of 0xAB shifted into bits 0-3 of byte1)
            Assert.Equal(0xB5, w.GetBuffer()[0]);
            Assert.Equal(0x0A, w.GetBuffer()[1]);
            Assert.Equal(12, w.BitPosition);
        }

        [Fact]
        public void WriteFloat_OnePointZero_IEEE754LE()
        {
            var w = new TelemetryBitWriter(4);
            w.WriteFloat(1.0f);
            // 1.0f in IEEE 754 LE = 00 00 80 3F
            Assert.Equal(0x00, w.GetBuffer()[0]);
            Assert.Equal(0x00, w.GetBuffer()[1]);
            Assert.Equal(0x80, w.GetBuffer()[2]);
            Assert.Equal(0x3F, w.GetBuffer()[3]);
        }

        [Fact]
        public void WriteDouble_OnePointZero_IEEE754LE()
        {
            var w = new TelemetryBitWriter(8);
            w.WriteDouble(1.0);
            // 1.0 in IEEE 754 LE = 00 00 00 00 00 00 F0 3F
            byte[] expected = { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x3F };
            Assert.Equal(expected, w.GetBuffer());
        }

        [Fact]
        public void WriteBits_ThenFloat_FloatStartsAtCorrectOffset()
        {
            // 10 bits then a float (32 bits) — float should occupy bits 10..41
            var w = new TelemetryBitWriter(6);
            w.WriteBits(0x3FF, 10);
            w.WriteFloat(1.0f);
            Assert.Equal(42, w.BitPosition);
        }

        [Fact]
        public void Reset_ClearsBufferAndPosition()
        {
            var w = new TelemetryBitWriter(4);
            w.WriteBits(0xFFFFFFFF, 32);
            w.Reset();
            Assert.Equal(0, w.BitPosition);
            Assert.All(w.GetBuffer(), b => Assert.Equal(0, b));
        }

        [Fact]
        public void ExternalBuffer_WritesAtOffset()
        {
            var buffer = new byte[10];
            for (int i = 0; i < buffer.Length; i++) buffer[i] = 0xCC;

            var w = new TelemetryBitWriter(buffer, 3, 4);
            w.Reset();  // Clears bytes 3..6
            w.WriteBits(0xAB, 8);

            Assert.Equal(0xCC, buffer[0]);
            Assert.Equal(0xCC, buffer[2]);
            Assert.Equal(0xAB, buffer[3]);
            Assert.Equal(0x00, buffer[4]);
            Assert.Equal(0xCC, buffer[7]);
        }

        [Fact]
        public void WriteBits_Overflow_Throws()
        {
            var w = new TelemetryBitWriter(1);
            Assert.Throws<InvalidOperationException>(() => w.WriteBits(0xFFFF, 16));
        }

        [Fact]
        public void WriteBits_ZeroBits_NoOp()
        {
            var w = new TelemetryBitWriter(1);
            w.WriteBits(0xFF, 0);
            Assert.Equal(0, w.BitPosition);
            Assert.Equal(0, w.GetBuffer()[0]);
        }
    }
}
