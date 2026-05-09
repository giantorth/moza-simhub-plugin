namespace MozaPlugin.Telemetry2.Sessions
{
    // How a chunk stream should be retransmitted on the wire. The policy is a simple
    // data-only descriptor consumed by the host's tick loop; the host owns the timer.
    //
    // PitHouse blind-retransmits session 0x01 (tier-def TLV) ~10× at 200ms intervals
    // because the wheel never FC-acks that session (per docs/protocol/findings/
    // 2026-05-02-tier-def-retransmission.md, confirmed by retransmit audits showing
    // every seq appears 3-5+ times with byte-identical content). Other sessions
    // (0x02, 0x04, 0x09) are FC-acked and need no blind retransmit.
    public readonly struct RetransmitPolicy
    {
        // Total times the same chunks are sent. 1 = fire-and-forget, no retransmit.
        public int Rounds { get; }

        // Milliseconds between successive rounds. Ignored when Rounds <= 1.
        public int IntervalMs { get; }

        public RetransmitPolicy(int rounds, int intervalMs)
        {
            Rounds = rounds < 1 ? 1 : rounds;
            IntervalMs = intervalMs < 0 ? 0 : intervalMs;
        }

        // Default: fire once, no retransmit. Suitable for sessions the wheel acks.
        public static readonly RetransmitPolicy NoRetransmit = new RetransmitPolicy(1, 0);

        // PitHouse blind-retransmit pattern for session 0x01 (tier-def TLV).
        public static readonly RetransmitPolicy TierDefBlind = new RetransmitPolicy(10, 200);

        public bool IsBlind => Rounds > 1;
    }
}
