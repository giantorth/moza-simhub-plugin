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
                    if (gameValue < 0)
                        return (uint)((int)gameValue & 0x1F); // -1 → 31 (5-bit two's complement; used for reverse gear)
                    return Math.Min((uint)gameValue, 30u);

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
                    MozaLog.Debug($"[Moza] TelemetryEncoder: unknown compression '{compression}', raw cast");
                    return (uint)(int)gameValue;
            }
        }

        private static double Clamp(double value, double min, double max)
        {
            return value < min ? min : value > max ? max : value;
        }

        /// <summary>
        /// Decoded-value min/max for test-pattern cycling.
        /// Ranges chosen so a triangle wave in [min..max] sweeps the displayable range
        /// of the target channel (LED bars, meters, numeric readouts).
        /// </summary>
        public static (double min, double max) GetTestRange(string compression)
        {
            switch (compression)
            {
                case "bool":            return (0.0, 1.0);
                case "uint3":
                case "uint8":
                case "uint15":          return (0.0, 15.0);     // 4-bit raw range
                case "int30":
                case "uint30":
                case "uint31":          return (0.0, 30.0);     // 5-bit forward-only (skip 31 = reverse)
                case "int8_t":          return (-128.0, 127.0);
                case "uint8_t":         return (0.0, 255.0);
                case "percent_1":       return (0.0, 100.0);
                case "float_001":       return (0.0, 1.0);
                case "tyre_pressure_1": return (0.0, 40.0);     // bar
                case "tyre_temp_1":     return (0.0, 150.0);    // °C
                case "track_temp_1":    return (0.0, 60.0);     // °C
                case "oil_pressure_1":  return (0.0, 10.0);     // bar
                case "int16_t":         return (-1000.0, 1000.0);
                case "uint16_t":        return (0.0, 10000.0);  // covers RPM
                case "float_6000_1":    return (0.0, 400.0);    // kmh
                case "float_600_2":     return (0.0, 400.0);
                case "brake_temp_1":    return (0.0, 1000.0);   // °C
                case "uint24_t":        return (0.0, 10000.0);
                case "int32_t":         return (-10000.0, 10000.0);
                case "uint32_t":        return (0.0, 10000.0);
                case "float":           return (0.0, 200.0);
                case "double":          return (0.0, 200.0);
                case "location_t":      return (0.0, 1.0);
                case "int64_t":         return (-1000.0, 1000.0);
                case "uint64_t":        return (0.0, 1000.0);
                default:                return (0.0, 1.0);
            }
        }
    }
}
