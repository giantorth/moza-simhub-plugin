using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace MozaPlugin.Diagnostics
{
    /// <summary>
    /// Process-wide ring buffer of timestamped serial frames in both directions.
    /// Off by default; turned on/off from the Diagnostics tab. No data persists
    /// to disk — the buffer lives in memory and is cleared every Start().
    /// </summary>
    public sealed class SerialTrafficCapture
    {
        public static SerialTrafficCapture Instance { get; } = new SerialTrafficCapture();

        // Entry cap — ring discards oldest once exceeded so a long capture can't
        // exhaust process memory. ~200k frames at typical telemetry rates ≈
        // 30–60 minutes of continuous traffic; older bytes drop silently.
        private const int MaxEntries = 200_000;

        public enum Direction : byte { Tx = (byte)'T', Rx = (byte)'R' }

        public sealed class Entry
        {
            public DateTime TimestampUtc;
            public Direction Dir;
            public string Source = string.Empty;
            public byte[] Bytes = Array.Empty<byte>();
        }

        private readonly ConcurrentQueue<Entry> _entries = new ConcurrentQueue<Entry>();
        private int _count;
        private volatile bool _enabled;
        private DateTime _startedAtUtc;

        public bool Enabled => _enabled;
        public int Count => Volatile.Read(ref _count);
        public DateTime StartedAtUtc => _startedAtUtc;

        private SerialTrafficCapture() { }

        public void Start()
        {
            Clear();
            _startedAtUtc = DateTime.UtcNow;
            _enabled = true;
        }

        /// <summary>Stop capture and return a snapshot of the recorded entries in order.</summary>
        public IReadOnlyList<Entry> Stop()
        {
            _enabled = false;
            var list = new List<Entry>(Volatile.Read(ref _count));
            foreach (var e in _entries)
                list.Add(e);
            return list;
        }

        public void Clear()
        {
            while (_entries.TryDequeue(out _)) { }
            Volatile.Write(ref _count, 0);
        }

        public void RecordTx(string source, byte[] frame) => Record(Direction.Tx, source, frame);
        public void RecordRx(string source, byte[] frame) => Record(Direction.Rx, source, frame);

        private void Record(Direction dir, string source, byte[] frame)
        {
            if (!_enabled || frame == null || frame.Length == 0) return;
            // Copy — caller buffers (e.g. read-loop tmp buffer) get reused.
            var copy = new byte[frame.Length];
            Buffer.BlockCopy(frame, 0, copy, 0, frame.Length);
            _entries.Enqueue(new Entry
            {
                TimestampUtc = DateTime.UtcNow,
                Dir = dir,
                Source = source ?? string.Empty,
                Bytes = copy,
            });
            int n = Interlocked.Increment(ref _count);
            // Ring trim: drop oldest until back inside cap. Concurrent producers
            // can over-trim by a few entries; that is fine — cap is approximate.
            while (n > MaxEntries && _entries.TryDequeue(out _))
                n = Interlocked.Decrement(ref _count);
        }

        /// <summary>
        /// Render entries as one-line-per-frame text. Timestamps are local time
        /// to ms; bytes are space-separated uppercase hex with no prefix.
        /// </summary>
        public static string Format(IReadOnlyList<Entry> entries)
        {
            var sb = new StringBuilder(entries.Count * 64);
            sb.Append("# timestamp (local)        dir source     bytes\n");
            foreach (var e in entries)
            {
                var local = e.TimestampUtc.ToLocalTime();
                sb.Append(local.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                sb.Append(' ');
                sb.Append((char)e.Dir);
                sb.Append("  ");
                sb.Append(e.Source.PadRight(10));
                sb.Append(' ');
                AppendHex(sb, e.Bytes);
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static void AppendHex(StringBuilder sb, byte[] data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(HexChar(data[i] >> 4));
                sb.Append(HexChar(data[i] & 0xF));
            }
        }

        private static char HexChar(int n) => (char)(n < 10 ? '0' + n : 'A' + (n - 10));
    }
}
