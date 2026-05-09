using MozaPlugin.Telemetry2.Sessions;

namespace MozaPlugin.Telemetry2.Operations
{
    // Placeholder for session 0x0B dashboard download. Phase-2 protocol (the actual
    // file-data transfer after staging-ack) is not yet decoded — see
    // docs/protocol/findings/2026-05-04-download-phase2-status.md. Until that decode
    // task lands, this op is a no-op consumer that claims the session so unclaimed-
    // session warnings don't surface on the host.
    //
    // The existing Telemetry/DashboardDownloader.cs (924 LOC) stays in-tree under the
    // old pipeline as reference material. The new pipeline ships without download
    // until phase 2 of bridge-20260501-073603.jsonl is fully decoded.
    public sealed class DashboardDownloadOp : ISessionConsumer
    {
        public const byte Session = 0x0B;
        public bool IsSupported => false;

        public void OnData(byte session, int seq, byte[] payload) { }
        public void OnAck(byte session, int ackSeq) { }
        public void OnOpen(byte session, int openSeq) { }
        public void OnClose(byte session, int ackSeq) { }
    }
}
