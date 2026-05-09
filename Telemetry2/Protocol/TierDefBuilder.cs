using System;
using System.Collections.Generic;
using System.IO;
using MozaPlugin.Telemetry2.Wire;

namespace MozaPlugin.Telemetry2.Protocol
{
    public enum FirmwareEra
    {
        // Numeric tier-def — current CSP / KSP / W17 firmware. Captures 2026-04 onward.
        // Channel records carry [idx, comp, bw, 0]; preamble + sections + END watermark.
        V2Type02 = 1,
        // Same wire shape but legacy alphabetic channel-index assignment (no wheel catalog).
        // Used only when the wheel hasn't advertised its catalog yet (older firmware).
        V2Flat = 2,
        // V0 URL-subscription path (legacy ES wheels). Different body shape; placeholder
        // for now — Phase 3 covers V2 first; V0 follows once a V0 reference capture is decoded.
        V0Url = 3,
    }

    // One sub-tier: a flag byte and its channel records.
    public readonly struct TierSpec
    {
        public byte Flag { get; }
        public IReadOnlyList<ChannelRecord> Channels { get; }

        public TierSpec(byte flag, IReadOnlyList<ChannelRecord> channels)
        {
            Flag = flag;
            Channels = channels ?? throw new ArgumentNullException(nameof(channels));
        }
    }

    // Pure builder. Same inputs → same bytes, every time. The negotiator (Phase 4) owns
    // the state machine that decides what to feed; this class just emits bytes per the
    // PitHouse-decoded structural rules.
    //
    // Rules from docs/protocol/findings/2026-05-04-tierdef-reference.md:
    //   * First emission of a session starts with [tag=07 PROTO_VER=2][tag=03 FLAG_BASE].
    //   * Each section = [tag=00 ENABLE × prevSectionFlags] + [tag=01 TIER × N] + [tag=06 END].
    //   * Section 1 of session has no ENABLEs (empty prev list).
    //   * END_MARKER = max channel index ever referenced anywhere in the session up to this section.
    //   * Channel records = [idx:u32LE][comp:u32LE][bw:u32LE][reserved:u32LE = 0].
    //   * No tag=04 URL records in V2 traffic.
    public static class TierDefBuilder
    {
        // Emit the one-shot preamble. Called only on the first message of a session.
        // 14 bytes: PROTO_VER (9B) + FLAG_BASE (5B).
        public static byte[] BuildPreamble(uint protoVersion = 2)
        {
            using var ms = new MemoryStream(14);
            using var w = new BinaryWriter(ms);
            TlvRecord.ProtoVersion(protoVersion).WriteTo(w);
            TlvRecord.FlagBase().WriteTo(w);
            return ms.ToArray();
        }

        // Emit one section: ENABLEs of prior-section flags, then new TIERs, then END.
        // Caller passes the cumulative session-wide max channel index as endMarkerValue
        // (the watermark rule decoded in Phase 0). For the very first section of a
        // session the watermark is typically 0; thereafter it tracks the session-wide max.
        public static byte[] BuildSection(
            IReadOnlyList<byte> prevSectionFlags,
            IReadOnlyList<TierSpec> tiers,
            uint endMarkerValue)
        {
            if (tiers == null || tiers.Count == 0)
                throw new ArgumentException("tiers must contain at least one entry", nameof(tiers));

            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            if (prevSectionFlags != null)
            {
                foreach (byte f in prevSectionFlags)
                    TlvRecord.EnablePrev(f).WriteTo(w);
            }

            foreach (var tier in tiers)
            {
                var channels = new ChannelRecord[tier.Channels.Count];
                for (int i = 0; i < channels.Length; i++) channels[i] = tier.Channels[i];
                TlvRecord.Tier(tier.Flag, channels).WriteTo(w);
            }

            TlvRecord.EndMarker(endMarkerValue).WriteTo(w);
            return ms.ToArray();
        }

        // Convenience: emit preamble + first section of a session in one byte[]. Equivalent
        // to BuildPreamble() concat BuildSection(empty, tiers, endMarkerValue).
        public static byte[] BuildInitialEmission(IReadOnlyList<TierSpec> tiers, uint endMarkerValue)
        {
            byte[] preamble = BuildPreamble();
            byte[] section = BuildSection(Array.Empty<byte>(), tiers, endMarkerValue);
            byte[] result = new byte[preamble.Length + section.Length];
            Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
            Buffer.BlockCopy(section, 0, result, preamble.Length, section.Length);
            return result;
        }

        // Helper for building TierSpecs from a profile + channel-index resolver. The
        // negotiator typically owns a `Func<string, uint>` that maps mzdash channel URLs
        // to wheel-catalog indices; this helper composes that with the compression-code
        // map to produce concrete ChannelRecord entries.
        //
        // Bit-width comes from the channel definition (mzdash); compression code from
        // CompressionTable. If a channel name isn't in the table, throws — caller must
        // catch and surface as a config error.
        public static TierSpec BuildTierFromUrls(byte flag,
            IReadOnlyList<(string url, string compressionName, int bitWidth)> channels,
            Func<string, uint> urlToIndex)
        {
            var records = new ChannelRecord[channels.Count];
            for (int i = 0; i < channels.Count; i++)
            {
                var (url, compName, bw) = channels[i];
                uint idx = urlToIndex(url);
                uint compCode = CompressionTable.GetByName(compName).Code;
                records[i] = new ChannelRecord(idx, compCode, (uint)bw);
            }
            return new TierSpec(flag, records);
        }
    }
}
