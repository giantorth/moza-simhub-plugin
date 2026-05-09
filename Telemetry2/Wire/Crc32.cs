namespace MozaPlugin.Telemetry2.Wire
{
    public static class Crc32
    {
        // ISO 3309 / zlib / Ethernet polynomial (reflected 0xEDB88320), init 0xFFFFFFFF, xor-out 0xFFFFFFFF.
        // Same algorithm as Telemetry/TierDefinitionBuilder.Crc32 — kept in one place here so every
        // Wire-layer type uses the same primitive. Verified against zlib.crc32 across all bridge captures.
        public static uint Compute(byte[] data, int offset, int length)
        {
            uint crc = 0xFFFFFFFF;
            int end = offset + length;
            for (int i = offset; i < end; i++)
            {
                crc ^= data[i];
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
            return crc ^ 0xFFFFFFFF;
        }

        public static uint Compute(byte[] data) => Compute(data, 0, data.Length);
    }
}
