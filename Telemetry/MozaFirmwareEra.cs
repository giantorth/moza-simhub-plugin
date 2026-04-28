namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Firmware era of the connected Moza wheel/base. Drives both tier-definition
    /// variant and dashboard-upload wire format. Replaces the legacy
    /// <c>TelemetryProtocolVersion</c> integer setting which only controlled the
    /// tier-def variant in isolation.
    ///
    /// Era inference is documented in <c>docs/protocol/FIRMWARE.md</c>. Mapping
    /// to internal toggles lives in <c>MozaPlugin.ApplyTelemetrySettings</c>.
    /// </summary>
    public enum MozaFirmwareEra
    {
        /// <summary>
        /// Use plugin defaults: tier-def v2 compact + 2026-04 upload wire format
        /// with auto-fallback to legacy on sub-msg 1 timeout. Recommended for
        /// users who don't know their firmware era.
        /// </summary>
        Auto = 0,

        /// <summary>
        /// 2025-11 firmware (VGS, CS V2.1). Tier-def v2 compact numeric +
        /// legacy 8-byte file-transfer header.
        /// </summary>
        Legacy2025_11 = 1,

        /// <summary>
        /// 2026-04+ firmware (KS Pro, current PitHouse). Tier-def v2 compact +
        /// new 6-byte file-transfer header.
        /// </summary>
        Modern2026_04 = 2,

        /// <summary>
        /// CSP on older firmware — tier-def v0 URL-subscription form +
        /// legacy 2025-11 upload wire. CSPs running modern firmware should
        /// pick <see cref="Modern2026_04"/> instead (URL-subscription is a
        /// firmware-version marker, not a hardware property).
        /// </summary>
        CspUrlSubscription = 3,
    }
}
