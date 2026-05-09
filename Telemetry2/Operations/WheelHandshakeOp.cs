using System;
using System.Collections.Generic;
using MozaPlugin.Telemetry2.Wire;

namespace MozaPlugin.Telemetry2.Operations
{
    // Emits the cold-start init sequence on h2b session 0x02 (per
    // docs/protocol/findings/2026-05-04-init-sequence.md):
    //   1. kind=2  timestamp init   [unix_ts u32][0 u32][tz_offset i32]
    //   2. kind=7  init command     [3 u32][0 u32]
    //   3. kind=8  channel catalog  zlib-compressed (caller supplies the payload)
    //   4. kind=11 action catalog   zlib-compressed (firmware-static, embedded)
    //
    // Once these four FF records have been emitted the handshake is done; the host
    // transitions to the Ready state. The op is one-shot per session; caller calls
    // BuildInitSequence() once and then KeepaliveOp takes over.
    //
    // The op does NOT push bytes to the wire by itself. It returns the FfRecords for
    // the caller to chunk via SessionEndpoint and frame via the existing MOZA primitives.
    public sealed class WheelHandshakeOp
    {
        // Action catalog (kind=11) is firmware-static — every cold-start capture observed
        // ships byte-identical 2568B zlib payload (per Phase 0 finding). Caller supplies
        // it as an embedded resource; the op doesn't bake it in to keep this layer pure.
        // ChannelCatalogPayload / ActionCatalogPayload removed 2026-05-05 — see
        // BuildInitSequence comment for context (kind=8/11 dropped from cold-start).

        // Build the cold-start init FF records.
        //
        // PitHouse sends {kind=2, 7, 8, 11} on every cold-start. kind=8 is the
        // channel catalog (dashboard-scoped, varies by active dash) and kind=11
        // is the action catalog (firmware-static 2572B). Both are zlib-compressed.
        // Currently we send only {2, 7}; kind=8/11 are accepted as optional params
        // when the caller has valid payloads.
        public IList<FfRecord> BuildInitSequence(DateTimeOffset now, int tzOffsetSeconds,
            byte[]? channelCatalogZlib = null, byte[]? actionCatalogZlib = null)
        {
            uint unix = (uint)now.ToUnixTimeSeconds();
            var seq = new List<FfRecord>(4);
            seq.Add(FfRecord.TimestampInit(unix, tzOffsetSeconds));
            seq.Add(FfRecord.InitCommand());
            if (channelCatalogZlib != null)
                seq.Add(FfRecord.ChannelCatalog(channelCatalogZlib));
            if (actionCatalogZlib != null)
                seq.Add(FfRecord.ActionCatalog(actionCatalogZlib));
            return seq;
        }

        // Convenience overload: tz offset from local TimeZoneInfo at the given time.
        public IList<FfRecord> BuildInitSequence(DateTimeOffset now)
        {
            int tz = (int)TimeZoneInfo.Local.GetUtcOffset(now.LocalDateTime).TotalSeconds;
            return BuildInitSequence(now, tz);
        }
    }
}
