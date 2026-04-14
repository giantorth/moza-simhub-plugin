using System;

namespace MozaPlugin.Tests
{
    /// <summary>
    /// Hex string parsing helper for test fixtures.
    /// Accepts space-separated hex bytes like "7e 18 43 17".
    /// </summary>
    internal static class HexUtil
    {
        public static byte[] Parse(string hex)
        {
            string[] tokens = hex.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new byte[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
                result[i] = Convert.ToByte(tokens[i], 16);
            return result;
        }

        public static string Format(byte[] data)
        {
            return BitConverter.ToString(data).Replace("-", " ").ToLowerInvariant();
        }
    }
}
