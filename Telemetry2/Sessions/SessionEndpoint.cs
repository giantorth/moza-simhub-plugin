using System.Collections.Generic;
using MozaPlugin.Telemetry2.Wire;

namespace MozaPlugin.Telemetry2.Sessions
{
    // Owns one session byte's outbound state. Tracks the next-seq counter, applies the
    // session's RetransmitPolicy, and chunks logical messages into SessionChunk frames.
    //
    // Replaces the scattered _session01OutboundSeq / _session02OutboundSeq fields in
    // Telemetry/TelemetrySender.cs (lines 94, 120) with a single per-session object.
    //
    // Inbound dispatch is the SessionDispatcher's job; SessionEndpoint is outbound-only.
    public sealed class SessionEndpoint
    {
        private readonly object _lock = new object();
        private ushort _nextSeq;

        public byte Session { get; }
        public RetransmitPolicy Policy { get; }

        public SessionEndpoint(byte session, RetransmitPolicy policy, ushort startSeq = 0)
        {
            Session = session;
            Policy = policy;
            _nextSeq = startSeq;
        }

        public ushort NextSeq
        {
            get { lock (_lock) return _nextSeq; }
        }

        // Reset seq counter (used on session close/reopen). Also called when the
        // host transitions Disconnected → Handshaking.
        public void ResetSeq(ushort to = 0)
        {
            lock (_lock) _nextSeq = to;
        }

        // Chunk a logical message into one-or-more SessionChunks, advancing the seq
        // counter by exactly the number of chunks emitted. Caller is responsible for
        // wrapping each chunk in a MOZA frame (via Protocol/MozaProtocol primitives)
        // and pushing it onto the serial transport.
        //
        // The returned list is one round's worth. If Policy.IsBlind, the host repeats
        // the same chunks Policy.Rounds times at Policy.IntervalMs spacing — but the
        // seq counter only advances once for the whole batch (retransmits use the
        // same seqs, per PitHouse convention confirmed in retransmit audits).
        public IList<SessionChunk> SendMessage(byte[] message)
        {
            lock (_lock)
            {
                var (chunks, nextSeq) = SessionChunk.ChunkMessage(message, Session, _nextSeq);
                _nextSeq = nextSeq;
                return chunks;
            }
        }

        // Build a single SessionChunk for an explicit type (data / open / close).
        // Useful for emitting close/open frames where chunking doesn't apply.
        public SessionChunk MakeChunk(SessionChunkType type, byte[]? payload)
        {
            lock (_lock)
            {
                ushort seq = _nextSeq;
                _nextSeq++;
                return new SessionChunk(Session, type, seq, payload ?? System.Array.Empty<byte>());
            }
        }
    }
}
