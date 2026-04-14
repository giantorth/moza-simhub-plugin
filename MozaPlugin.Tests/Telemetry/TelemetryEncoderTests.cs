using MozaPlugin.Telemetry;
using Xunit;

namespace MozaPlugin.Tests.Telemetry
{
    public class TelemetryEncoderTests
    {
        [Theory]
        [InlineData(0.0, 0u)]
        [InlineData(1.0, 1u)]
        [InlineData(-5.0, 1u)]
        [InlineData(0.5, 1u)]
        public void Encode_Bool(double value, uint expected)
        {
            Assert.Equal(expected, TelemetryEncoder.Encode("bool", value));
        }

        [Theory]
        [InlineData("uint3", 0.0, 0u)]
        [InlineData("uint3", 3.0, 3u)]
        [InlineData("uint3", 20.0, 15u)]   // clamped at 15
        [InlineData("uint3", -5.0, 0u)]    // clamped at 0
        [InlineData("uint8", 7.0, 7u)]
        [InlineData("uint15", 12.0, 12u)]
        public void Encode_Uint3Family_ClampedTo0_15(string compression, double value, uint expected)
        {
            Assert.Equal(expected, TelemetryEncoder.Encode(compression, value));
        }

        [Theory]
        [InlineData(0.0, 0u)]
        [InlineData(6.0, 6u)]
        [InlineData(30.0, 30u)]
        [InlineData(35.0, 30u)]   // clamped at 30
        [InlineData(-1.0, 31u)]   // reverse gear: 5-bit two's complement
        public void Encode_Int30(double value, uint expected)
        {
            Assert.Equal(expected, TelemetryEncoder.Encode("int30", value));
        }

        [Fact]
        public void Encode_Int30_NegativeAlias()
        {
            Assert.Equal(31u, TelemetryEncoder.Encode("uint30", -1.0));
            Assert.Equal(31u, TelemetryEncoder.Encode("uint31", -1.0));
        }

        [Theory]
        [InlineData(0.0, 0u)]
        [InlineData(0.5, 500u)]
        [InlineData(1.0, 1000u)]
        [InlineData(2.0, 1000u)]   // clamped
        [InlineData(-0.5, 0u)]     // clamped
        public void Encode_Float001(double value, uint expected)
        {
            Assert.Equal(expected, TelemetryEncoder.Encode("float_001", value));
        }

        [Theory]
        [InlineData(0.0, 0u)]
        [InlineData(50.0, 500u)]
        [InlineData(100.0, 1000u)]
        [InlineData(150.0, 1000u)]  // clamped
        public void Encode_Percent1(double value, uint expected)
        {
            Assert.Equal(expected, TelemetryEncoder.Encode("percent_1", value));
        }

        [Theory]
        [InlineData(0.0, 0u)]
        [InlineData(100.0, 1000u)]
        [InlineData(6553.5, 65535u)]
        [InlineData(7000.0, 65535u)]  // clamped
        public void Encode_Float6000_1(double value, uint expected)
        {
            Assert.Equal(expected, TelemetryEncoder.Encode("float_6000_1", value));
        }

        [Theory]
        [InlineData(0.0, 0u)]
        [InlineData(3.14, 314u)]
        [InlineData(655.35, 65535u)]
        public void Encode_Float600_2(double value, uint expected)
        {
            Assert.Equal(expected, TelemetryEncoder.Encode("float_600_2", value));
        }

        [Theory]
        [InlineData(0.0, 5000u)]      // baseline (offset)
        [InlineData(80.0, 5800u)]     // 80°C
        [InlineData(-500.0, 0u)]      // clamped low
        [InlineData(2000.0, 16383u)]  // clamped high
        public void Encode_TyreTemp1(double value, uint expected)
        {
            Assert.Equal(expected, TelemetryEncoder.Encode("tyre_temp_1", value));
        }

        [Fact]
        public void Encode_TyreTemp1_AliasTrackTempOilPressure()
        {
            Assert.Equal(5800u, TelemetryEncoder.Encode("track_temp_1", 80.0));
            Assert.Equal(5800u, TelemetryEncoder.Encode("oil_pressure_1", 80.0));
        }

        [Theory]
        [InlineData(0.0, 5000u)]
        [InlineData(400.0, 9000u)]
        public void Encode_BrakeTemp1(double value, uint expected)
        {
            Assert.Equal(expected, TelemetryEncoder.Encode("brake_temp_1", value));
        }

        [Theory]
        [InlineData(0.0, 0u)]
        [InlineData(100.0, 1000u)]
        [InlineData(500.0, 4095u)]   // clamped
        public void Encode_TyrePressure1(double value, uint expected)
        {
            Assert.Equal(expected, TelemetryEncoder.Encode("tyre_pressure_1", value));
        }

        [Theory]
        [InlineData(0.0, 0u)]
        [InlineData(1700.0, 1700u)]
        [InlineData(65535.0, 65535u)]
        [InlineData(-1.0, 65535u)]   // negative wraps via ushort cast
        public void Encode_Uint16(double value, uint expected)
        {
            Assert.Equal(expected, TelemetryEncoder.Encode("uint16_t", value));
        }

        [Theory]
        [InlineData("int8_t", 100.0, 100u)]
        [InlineData("uint8_t", 200.0, 200u)]
        [InlineData("uint8_t", -1.0, 255u)]
        public void Encode_Byte(string compression, double value, uint expected)
        {
            Assert.Equal(expected, TelemetryEncoder.Encode(compression, value));
        }

        [Theory]
        [InlineData(0.0, 0u)]
        [InlineData(1000.0, 1000u)]
        [InlineData(0xFFFFFF, 0xFFFFFFu)]
        public void Encode_Uint24(double value, uint expected)
        {
            Assert.Equal(expected, TelemetryEncoder.Encode("uint24_t", value));
        }

        [Fact]
        public void IsFloat_OnlyFloat()
        {
            Assert.True(TelemetryEncoder.IsFloat("float"));
            Assert.False(TelemetryEncoder.IsFloat("double"));
            Assert.False(TelemetryEncoder.IsFloat("float_001"));
            Assert.False(TelemetryEncoder.IsFloat("uint16_t"));
        }

        [Theory]
        [InlineData("double", true)]
        [InlineData("location_t", true)]
        [InlineData("int64_t", true)]
        [InlineData("uint64_t", true)]
        [InlineData("float", false)]
        [InlineData("uint16_t", false)]
        public void IsDouble(string compression, bool expected)
        {
            Assert.Equal(expected, TelemetryEncoder.IsDouble(compression));
        }

        [Theory]
        [InlineData("bool", 1)]
        [InlineData("uint3", 4)]
        [InlineData("int30", 5)]
        [InlineData("float_001", 10)]
        [InlineData("percent_1", 10)]
        [InlineData("tyre_pressure_1", 12)]
        [InlineData("tyre_temp_1", 14)]
        [InlineData("uint16_t", 16)]
        [InlineData("float_6000_1", 16)]
        [InlineData("uint24_t", 24)]
        [InlineData("float", 32)]
        [InlineData("double", 64)]
        public void BitWidths_KnownEntries(string compression, int expected)
        {
            Assert.Equal(expected, TelemetryEncoder.BitWidths[compression]);
        }
    }
}
