using System;
using System.Collections.Generic;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Converts game values to raw bit-packed integers using Moza compression type formulas.
    /// Formulas reverse-engineered from Pit House TelemetryBitFormat (pithouse-re.md § 9).
    /// </summary>
    public static class TelemetryEncoder
    {
        /// <summary>
        /// Bit widths for each compression type (pithouse-re.md § 1, Findings).
        /// </summary>
        public static readonly Dictionary<string, int> BitWidths = new Dictionary<string, int>
        {
            ["bool"]            = 1,
            ["uint3"]           = 4,
            ["uint8"]           = 4,
            ["uint15"]          = 4,
            ["int30"]           = 5,
            ["uint30"]          = 5,
            ["uint31"]          = 5,
            ["int8_t"]          = 8,
            ["uint8_t"]         = 8,
            ["float_001"]       = 10,
            ["percent_1"]       = 10,
            ["tyre_pressure_1"] = 12,
            ["tyre_temp_1"]     = 14,
            ["track_temp_1"]    = 14,
            ["oil_pressure_1"]  = 14,
            ["int16_t"]         = 16,
            ["uint16_t"]        = 16,
            ["float_6000_1"]    = 16,
            ["float_600_2"]     = 16,
            ["brake_temp_1"]    = 16,
            ["uint24_t"]        = 24,
            ["float"]           = 32,
            ["int32_t"]         = 32,
            ["uint32_t"]        = 32,
            ["double"]          = 64,
            ["location_t"]      = 64,
            ["int64_t"]         = 64,
            ["uint64_t"]        = 64,
        };

        /// <summary>
        /// Returns true if this compression type is a 64-bit double (use WriteDouble, not WriteBits).
        /// </summary>
        public static bool IsDouble(string compression)
        {
            switch (compression)
            {
                case "double": case "location_t": case "int64_t": case "uint64_t":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Returns true if this compression type is a 32-bit float (use WriteFloat, not WriteBits).
        /// </summary>
        public static bool IsFloat(string compression) => compression == "float";

        /// <summary>
        /// Encode a game value to a raw uint for WriteBits.
        /// For float/double types use WriteFloat/WriteDouble instead.
        /// </summary>
        public static uint Encode(string compression, double gameValue)
        {
            switch (compression)
            {
                case "bool":
                    return gameValue != 0.0 ? 1u : 0u;

                case "uint3":
                case "uint8":
                case "uint15":
                    return Math.Min((uint)Math.Max(0, gameValue), 15u);

                case "int30":
                case "uint30":
                case "uint31":
                    return Math.Min((uint)Math.Max(0, gameValue), 31u);

                case "int8_t":
                case "uint8_t":
                    return (uint)(byte)(int)gameValue;

                case "percent_1":
                    return (uint)Clamp(gameValue * 10.0, 0, 1000);

                case "float_001":
                    return (uint)Clamp(gameValue * 1000.0, 0, 1000);

                case "tyre_pressure_1":
                    return (uint)Clamp(gameValue * 10.0, 0, 4095);

                case "tyre_temp_1":
                case "track_temp_1":
                case "oil_pressure_1":
                    return (uint)Clamp(gameValue * 10.0 + 5000.0, 0, 16383);

                case "int16_t":
                case "uint16_t":
                    return (uint)(ushort)(int)gameValue;

                case "float_6000_1":
                    return (uint)Clamp(gameValue * 10.0, 0, 65535);

                case "float_600_2":
                    return (uint)Clamp(gameValue * 100.0, 0, 65535);

                case "brake_temp_1":
                    return (uint)Clamp(gameValue * 10.0 + 5000.0, 0, 65535);

                case "uint24_t":
                    return (uint)gameValue & 0xFFFFFF;

                case "int32_t":
                case "uint32_t":
                    return (uint)(int)gameValue;

                // float and double handled via WriteFloat/WriteDouble
                default:
                    return (uint)(int)gameValue;
            }
        }

        private static double Clamp(double value, double min, double max)
        {
            return value < min ? min : value > max ? max : value;
        }
    }
}
