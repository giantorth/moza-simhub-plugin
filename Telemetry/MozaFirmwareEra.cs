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
        /// Auto-detect at runtime. Tier-def emit path checks
        /// <c>_wheelChannelCatalog</c>: catalog advertised by wheel on session
        /// 0x02 → Type02 wire format (V2 + 6B + Type02); empty → legacy V2 + 6B
        /// with one-shot fallback to 8B on first sub-msg-1 timeout. Verified
        /// 2026-04-30 against R5+W17 (Type02) and CSP on R9 (legacy 6B).
        /// </summary>
        Auto = 0,

        /// <summary>Compact tier-def (V2) + 8-byte upload header.</summary>
        TierDefV2_Upload8B = 1,

        /// <summary>Compact tier-def (V2) + 6-byte upload header.</summary>
        TierDefV2_Upload6B = 2,

        // Reserved = 3 (was TierDefV0_Upload8B — never tested live, dropped 2026-04-30)

        /// <summary>URL-subscription tier-def (V0) + 6-byte upload header.</summary>
        TierDefV0_Upload6B = 4,

        /// <summary>Compact tier-def (V2) + 6-byte upload header + type-02
        /// metadata sub-msg layout. Required for post-2026-04 CSP / W17 firmware
        /// (verified `RS21-W17-MC SW` 2026-04-28). See
        /// <see cref="FileTransferWireFormat.New2026_04_Type02"/>.</summary>
        TierDefV2_Type02 = 5,

        // Reserved = 6 (was TierDefV0_Type02 — speculative, never observed live, dropped 2026-04-30)
    }
}
