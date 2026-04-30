using System;
using System.Collections.Generic;
using System.Linq;

namespace MozaPlugin.Diagnostics
{
    /// <summary>
    /// Reliable-stream retransmit queue for SerialStream session-data chunks
    /// (frames matching <c>7E N 43 17 7C 00 [session] [type=01] [seq_lo seq_hi]
    /// [payload] [crc32]</c>). PitHouse re-emits each unacked chunk continuously
    /// until the wheel acks via <c>fc:00 [session] [ack_seq:u16 LE]</c>; plugin
    /// previously fired-and-forgot, leaving session-02 chunk rate ~70× below
    /// PitHouse on the wire (2026-04-29 nebula diff).
    ///
    /// Usage:
    ///   1. After <c>_connection.Send(frame)</c> for a session-data chunk, call
    ///      <see cref="Track"/> to enqueue it.
    ///   2. In the fc:00 ack handler, call <see cref="Ack"/> with the parsed
    ///      session and ack_seq. All chunks with seq &lt;= ack_seq drop from the
    ///      session's queue.
    ///   3. Periodically call <see cref="DueRetransmits"/> and resend each
    ///      returned frame. Returns frames whose previous send was &gt;=
    ///      <paramref name="intervalMs"/> ago; chunks past
    ///      <paramref name="maxRetries"/> are dropped to bound queue size.
    /// </summary>
    public sealed class SessionRetransmitter
    {
        private sealed class Pending
        {
            public byte[] Frame = Array.Empty<byte>();
            public int LastSentTicks;
            public int SendCount;
        }

        private readonly Dictionary<(byte session, int seq), Pending> _queue
            = new Dictionary<(byte, int), Pending>();
        private readonly object _lock = new object();

        public int QueueSize { get { lock (_lock) return _queue.Count; } }

        /// <summary>
        /// Inspect <paramref name="frame"/>; if it's a session-data chunk on
        /// group 0x43 dev 0x17, enqueue it for retransmit. No-op otherwise.
        /// Frame must be the unstuffed wire form: <c>7E N 43 17 7C 00 sess
        /// type seq_lo seq_hi …</c>.
        /// </summary>
        public void Track(byte[] frame)
        {
            if (frame == null || frame.Length < 12) return;
            if (frame[0] != 0x7E) return;
            if (frame[2] != 0x43 || frame[3] != 0x17) return;
            if (frame[4] != 0x7C || frame[5] != 0x00) return;
            if (frame[7] != 0x01) return;  // data chunks only — skip type=00 ends and type=81 opens

            byte session = frame[6];
            int seq = frame[8] | (frame[9] << 8);
            var entry = new Pending
            {
                Frame = frame,
                LastSentTicks = Environment.TickCount,
                SendCount = 1,
            };
            lock (_lock)
            {
                _queue[(session, seq)] = entry;
            }
        }

        /// <summary>
        /// Drop all queued chunks for <paramref name="session"/> with seq &lt;=
        /// <paramref name="ackSeq"/>. Mirrors how PitHouse stops retransmitting
        /// on ack.
        /// </summary>
        public void Ack(byte session, int ackSeq)
        {
            lock (_lock)
            {
                var doomed = new List<(byte, int)>();
                foreach (var kv in _queue)
                {
                    if (kv.Key.session == session && kv.Key.seq <= ackSeq)
                        doomed.Add(kv.Key);
                }
                foreach (var k in doomed) _queue.Remove(k);
            }
        }

        /// <summary>
        /// Return frames whose last send was &gt;= <paramref name="intervalMs"/>
        /// ago. Chunks past <paramref name="maxRetries"/> sends are silently
        /// dropped from the queue (assume permanent loss).
        /// </summary>
        public List<byte[]> DueRetransmits(int intervalMs, int maxRetries)
        {
            int now = Environment.TickCount;
            var output = new List<byte[]>();
            lock (_lock)
            {
                var doomed = new List<(byte, int)>();
                foreach (var kv in _queue)
                {
                    if (now - kv.Value.LastSentTicks < intervalMs) continue;
                    if (kv.Value.SendCount >= maxRetries)
                    {
                        doomed.Add(kv.Key);
                        continue;
                    }
                    output.Add(kv.Value.Frame);
                    kv.Value.LastSentTicks = now;
                    kv.Value.SendCount++;
                }
                foreach (var k in doomed) _queue.Remove(k);
            }
            return output;
        }

        public void Clear()
        {
            lock (_lock) _queue.Clear();
        }
    }
}
