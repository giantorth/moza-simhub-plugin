using System;
using MozaPlugin.Telemetry;
using ISessionConsumer = MozaPlugin.Telemetry2.Sessions.ISessionConsumer;

namespace MozaPlugin.Telemetry2.Operations
{
    // Tile-server channel. Wheel pushes its tile-server state via this channel; the
    // active session byte depends on firmware era (per Telemetry/TelemetrySender.cs:2328):
    //   - 2025-11 firmware: session 0x03 (host-opened)
    //   - 2026-04+ firmware: session 0x0B (wheel-opened mirror)
    // We listen on both — wheel only pushes on one, parser drops malformed blobs.
    //
    // Outbound BuildOutboundState() builds an empty-state blob; caller sends on whichever
    // session is appropriate for the firmware (currently sess 0x03 host-side, but the
    // 2026-04+ doc says sess=0x03 is keepalive-only on that firmware — so don't push
    // tile-server state outbound on 2026-04+).
    public sealed class TileServerOp : ISessionConsumer
    {
        public const byte SessionLegacy = 0x03;   // 2025-11 firmware
        public const byte SessionKsPro  = 0x0B;   // 2026-04+ firmware
        public const byte Session = SessionLegacy;

        private readonly TileServerStateParser _parser = new TileServerStateParser();

        public TileServerState? LastState => _parser.LastState;

        // Outbound: build the empty-state blob (envelope + zlib JSON describing no maps).
        // Caller sends it once on session open.
        public byte[] BuildOutboundState()
        {
            byte[] json = TileServerStateBuilder.BuildEmptyStateJson();
            return TileServerStateBuilder.BuildFullBlob(json);
        }

        public void OnData(byte session, int seq, byte[] payload)
        {
            if (session != SessionLegacy && session != SessionKsPro) return;
            _parser.OnChunk(payload);
        }

        public void OnAck(byte session, int ackSeq) { }
        public void OnOpen(byte session, int openSeq) { }
        public void OnClose(byte session, int ackSeq) { }
    }
}
