namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Wire-protocol combination for telemetry handshake. Each entry is a
    /// pairing of two orthogonal axes:
    ///
    ///   Tier-def axis:
    ///     V2 — compact numeric (single batch of flag bytes, channel indices,
    ///          compression codes, bit widths per tier).
    ///     V0 — URL subscription (host sends channel URLs; wheel resolves
    ///          compression internally).
    ///
    ///   Upload-header axis:
    ///     8B — 8-byte sub-msg header (`role/max_chunk/type/5×reserved`).
    ///     6B — 6-byte sub-msg header (`type/size_LE/3×reserved`).
    ///
    /// Naming reflects observable wire bytes only. Wheel/firmware mapping is
    /// captured in <c>docs/protocol/FIRMWARE.md</c> for reference. configJson
    /// and tile-server session locations are auto-detected by the plugin and
    /// don't need a setting.
    /// </summary>
    public enum MozaFirmwareEra
    {
        /// <summary>
        /// Plugin defaults: V2 compact tier-def + 6-byte upload header. Plugin
        /// auto-fallbacks the upload header to 8-byte on first sub-msg-1 timeout.
        /// </summary>
        Auto = 0,

        /// <summary>Compact tier-def (V2) + 8-byte upload header.</summary>
        TierDefV2_Upload8B = 1,

        /// <summary>Compact tier-def (V2) + 6-byte upload header.</summary>
        TierDefV2_Upload6B = 2,

        /// <summary>URL-subscription tier-def (V0) + 8-byte upload header.</summary>
        TierDefV0_Upload8B = 3,

        /// <summary>URL-subscription tier-def (V0) + 6-byte upload header.</summary>
        TierDefV0_Upload6B = 4,
    }
}
