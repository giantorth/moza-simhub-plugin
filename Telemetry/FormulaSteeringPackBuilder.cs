using System;
using System.Collections.Generic;
using System.Linq;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Speculative builder for the 18-pack FormulaSteering telemetry layout
    /// observed in MOZA Pit House (Apr 2026 build, md5 2b9ad3b5cb...).
    ///
    /// Status: WIRE FORMAT NOT CONFIRMED.
    /// Pit House defines 18 Pack classes
    /// (<c>Protocol::FormulaSteeringTelemetryDataPack1..18</c>) each carrying a
    /// 13–20 byte payload. Pack constructors store byte 0x42 ('B') and a per-pack
    /// payload size at fixed offsets. Whether 0x42 reaches the wire as a TLV
    /// tag, gets re-wrapped, or stays internal is unknown — only two startup
    /// captures (<c>usb-capture/ksp/{mozahubstartup,putOnWheelAndOpenPitHouse}.pcapng</c>)
    /// have been searched, neither with active game telemetry, so the absence
    /// is uninformative. The Packs may feed into the existing bit-packed
    /// <c>cmd=0x43, dev=0x17, sub=7d:23</c> flow implemented by
    /// <see cref="TelemetryFrameBuilder"/> and <see cref="TierDefinitionBuilder"/>,
    /// or they may use a separate envelope yet to be observed.
    ///
    /// What this builder does:
    /// 1. Provides the per-pack payload-byte budgets extracted from the Pit
    ///    House binary so we can validate any future tier-def we send to a
    ///    KS Pro that subscribes via FormulaSteering.
    /// 2. Distributes a flat channel list across up to 18 tiers using those
    ///    budgets, each tier at the channel's <c>PackageLevel</c> rate.
    ///
    /// Resulting <see cref="MultiStreamProfile"/> plugs into the existing
    /// telemetry sender as a tier-def candidate. Once a KS Pro game-running
    /// capture confirms (or refutes) the pack→tier mapping and the wire
    /// envelope, revisit the allocation strategy in <see cref="BuildProfile"/>.
    /// </summary>
    public static class FormulaSteeringPackBuilder
    {
        /// <summary>
        /// Per-pack payload budget in bytes, indexed [0..17] → Pack1..Pack18.
        /// Extracted from the Pit House binary by reading the
        /// <c>mov dword [eax+0x0c], imm32</c> immediate inside each pack's
        /// constructor (file offsets 0x13764e7..0x138648e). Pack11's opcode
        /// form differs and was not extracted; using 19 as a placeholder
        /// (median of neighbouring packs).
        /// </summary>
        public static readonly int[] PackBudgetBytes =
        {
            20, // Pack1
            13, // Pack2
            14, // Pack3
            18, // Pack4
            20, // Pack5
            20, // Pack6
            20, // Pack7
            18, // Pack8
            19, // Pack9
            20, // Pack10
            19, // Pack11 — TBD (binary opcode form not parsed)
            13, // Pack12
            20, // Pack13
            19, // Pack14
            19, // Pack15
            19, // Pack16
            20, // Pack17
            20, // Pack18
        };

        public const int PackCount = 18;

        /// <summary>
        /// Total bytes available across all 18 packs.
        /// Sum of <see cref="PackBudgetBytes"/> = 341 bytes per cycle (assuming
        /// every pack fires every tick — actual wire rate depends on each
        /// pack's <c>TelemetryDataTimedForwardingWorker</c> tick interval).
        /// </summary>
        public static int TotalBudgetBytes => PackBudgetBytes.Sum();

        /// <summary>
        /// Distribute channels across up to 18 tiers grouped by package_level
        /// rate, packing channels into each tier until the per-pack byte
        /// budget is hit. Tiers are sorted by package_level ascending so flag
        /// byte offsets match the existing convention (<c>flagBase + i</c>).
        /// </summary>
        /// <param name="name">Profile name (passed through to MultiStreamProfile).</param>
        /// <param name="channels">All candidate channels. Sorted internally.</param>
        /// <param name="maxTiers">Cap on tiers produced; clamped to 18.</param>
        public static MultiStreamProfile BuildProfile(
            string name,
            IEnumerable<ChannelDefinition> channels,
            int maxTiers = PackCount)
        {
            if (maxTiers < 1) maxTiers = 1;
            if (maxTiers > PackCount) maxTiers = PackCount;

            // Group by package_level so each tier carries one update rate.
            var byLevel = channels
                .GroupBy(c => c.PackageLevel)
                .OrderBy(g => g.Key)
                .ToList();

            var tiers = new List<DashboardProfile>();
            int tierIndex = 0;

            foreach (var levelGroup in byLevel)
            {
                if (tierIndex >= maxTiers) break;

                // Sort channels in this level alphabetically by URL (matches
                // Pit House's std::map iteration order — see § 6 of pithouse-re.md).
                var levelChannels = levelGroup
                    .OrderBy(c => c.Url, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                int channelIdx = 0;
                while (channelIdx < levelChannels.Count && tierIndex < maxTiers)
                {
                    int budget = PackBudgetBytes[tierIndex];
                    var packed = new List<ChannelDefinition>();
                    int bits = 0;

                    while (channelIdx < levelChannels.Count)
                    {
                        var ch = levelChannels[channelIdx];
                        int newBits = bits + ch.BitWidth;
                        int newBytes = (newBits + 7) / 8;
                        if (newBytes > budget) break;
                        packed.Add(ch);
                        bits = newBits;
                        channelIdx++;
                    }

                    if (packed.Count == 0)
                    {
                        // First channel doesn't fit even alone — over-budget.
                        // Skip the tier and move on to keep the loop moving.
                        // (This shouldn't happen with sane inputs; the smallest
                        // pack budget is 13 bytes and the largest single
                        // channel is 8 bytes / 64 bits.)
                        tierIndex++;
                        continue;
                    }

                    var profile = new DashboardProfile
                    {
                        Name = $"{name}#Pack{tierIndex + 1}-L{levelGroup.Key}",
                        Channels = packed,
                        PackageLevel = levelGroup.Key,
                        TotalBits = bits,
                        TotalBytes = (bits + 7) / 8,
                    };
                    tiers.Add(profile);
                    tierIndex++;
                }
            }

            return new MultiStreamProfile { Name = name, Tiers = tiers };
        }
    }
}
