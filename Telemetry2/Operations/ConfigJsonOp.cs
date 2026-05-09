using System;
using MozaPlugin.Telemetry;
using ISessionConsumer = MozaPlugin.Telemetry2.Sessions.ISessionConsumer;

namespace MozaPlugin.Telemetry2.Operations
{
    // ConfigJson RPC consumer. Listens on session 0x09 only.
    //
    // Earlier code listened on both 0x09 and 0x0A based on the doc's claim that
    // KS Pro firmware moves configJson to 0x0A. Live capture of the user's wheel
    // (CSP/W17 post-2026-04 per settings) shows: sess=0x09 carries the standard
    // 9-byte compressed configJson envelope, and sess=0x0A carries DIFFERENT
    // content (TLV records, possibly tier-def echoes). Feeding sess=0x0A data
    // into ConfigJsonClient corrupts its reassembly buffer and produces
    // exceptions. The doc may apply to a different firmware variant we haven't
    // captured. For now, stay on sess=0x09 — matches v1 behavior on this wheel.
    //
    // If a future firmware does send configJson on sess=0x0A, we'd add a SECOND
    // ConfigJsonClient instance for it (separate buffer, no state corruption).
    public sealed class ConfigJsonOp : ISessionConsumer
    {
        public const byte Session = 0x09;

        private readonly ConfigJsonClient _client = new ConfigJsonClient();

        // Fired when a fresh wheel-dashboard-state blob arrives (deduped against last seen).
        public event EventHandler<WheelDashboardState>? WheelStateChanged;

        public WheelDashboardState? LastState => _client.LastState;

        public void OnData(byte session, int seq, byte[] payload)
        {
            if (session != Session || payload == null) return;
            var prev = _client.LastState;
            WheelDashboardState? fresh;
            try
            {
                fresh = _client.OnChunk(payload);
            }
            catch
            {
                return;
            }
            if (fresh != null && !ReferenceEquals(fresh, prev))
                WheelStateChanged?.Invoke(this, fresh);
        }

        public void Reset() => _client.Reset();

        public void OnAck(byte session, int ackSeq) { }
        public void OnOpen(byte session, int openSeq) { }
        public void OnClose(byte session, int ackSeq) { }
    }
}
