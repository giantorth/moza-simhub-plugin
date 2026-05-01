using System;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Builds session-0x01 host→wheel property push records (the `ff`-tagged
    /// format PitHouse uses for wheel-integrated dashboard runtime settings
    /// like display brightness and standby timeout).
    ///
    /// Net-data layout (caller wraps with the standard chunk container +
    /// outer chunk CRC32 via <see cref="MozaPlugin.Telemetry.TierDefinitionBuilder.ChunkMessage"/>):
    ///
    /// <code>
    /// ff &lt;size:u32 LE&gt; &lt;inner_crc32_LE:4&gt; &lt;kind:u32 LE&gt; &lt;value:size-4 bytes LE&gt;
    /// </code>
    ///
    /// where <c>size = 4 + sizeof(value)</c> and <c>inner_crc32 =
    /// zlib.crc32(kind ‖ value)</c>. See
    /// <c>docs/protocol/findings/2026-04-29-session-01-property-push.md</c>
    /// for the wire-format reverse-engineering and verified samples.
    /// </summary>
    public static class SessionPropertyPushBuilder
    {
        /// <summary>
        /// Property `kind` for dashboard display brightness (u32 0–100).
        /// </summary>
        public const uint KindDashBrightness = 1;

        /// <summary>
        /// Property `kind` for display standby timeout (u64 milliseconds).
        /// </summary>
        public const uint KindDashStandbyMs = 10;

        /// <summary>
        /// Field1 constant for the dashboard-switch FF-record. Verified
        /// in capture <c>automobilista-switch-dashboard-many-ends-on-grids-1.2.6.17.pcapng</c>
        /// and <c>wireshark/csp/startup, change knob colors, ...pcapng</c>.
        /// </summary>
        public const uint DashSwitchField1 = 4;

        /// <summary>
        /// Build the net-data body for a u32-valued property (e.g. brightness).
        /// </summary>
        public static byte[] BuildU32Body(uint kind, uint value)
        {
            // size = kind(4) + value(4) = 8
            var kv = new byte[8];
            WriteU32LE(kv, 0, kind);
            WriteU32LE(kv, 4, value);
            return WrapFfRecord(kv);
        }

        /// <summary>
        /// Build the net-data body for a u64-valued property (e.g. standby ms).
        /// </summary>
        public static byte[] BuildU64Body(uint kind, ulong value)
        {
            // size = kind(4) + value(8) = 12
            var kv = new byte[12];
            WriteU32LE(kv, 0, kind);
            WriteU64LE(kv, 4, value);
            return WrapFfRecord(kv);
        }

        /// <summary>
        /// Build the net-data body for a dashboard-switch command.
        /// <paramref name="slotIndex"/> is the <b>0-based</b> index into the
        /// wheel's <c>configJsonList</c> (alphabetical name list from session
        /// 0x09 state push). Verified 2026-04-30: slot=1 → configJsonList[1].
        /// See <c>docs/protocol/findings/2026-04-30-dashboard-switch-3f27.md</c>.
        /// </summary>
        public static byte[] BuildDashboardSwitchBody(uint slotIndex)
        {
            var kv = new byte[12];
            WriteU32LE(kv, 0, DashSwitchField1);  // field1 = 4
            WriteU32LE(kv, 4, slotIndex);          // field2 = 0-based configJsonList index
            WriteU32LE(kv, 8, 0u);                 // field3 = 0
            return WrapFfRecord(kv);
        }

        internal static byte[] WrapFfRecord(byte[] kindAndValue)
        {
            int size = kindAndValue.Length;                 // 4 + sizeof(value)
            uint innerCrc = global::MozaPlugin.Telemetry.TierDefinitionBuilder
                .Crc32(kindAndValue, 0, size);

            var body = new byte[1 + 4 + 4 + size];
            body[0] = 0xFF;
            WriteU32LE(body, 1, (uint)size);
            WriteU32LE(body, 5, innerCrc);
            Array.Copy(kindAndValue, 0, body, 9, size);
            return body;
        }

        private static void WriteU32LE(byte[] buf, int offset, uint value)
        {
            buf[offset]     = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
            buf[offset + 2] = (byte)((value >> 16) & 0xFF);
            buf[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static void WriteU64LE(byte[] buf, int offset, ulong value)
        {
            for (int i = 0; i < 8; i++)
                buf[offset + i] = (byte)((value >> (8 * i)) & 0xFF);
        }
    }
}
