using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace MozaPlugin.Diagnostics
{
    /// <summary>
    /// Process-wide ring buffer of timestamped serial frames in both directions,
    /// plus an optional always-on JSONL sink that mirrors the layout produced by
    /// <c>sim/bridge.py</c> so capture-comparison tooling works on either source.
    /// In-memory ring is cleared on every Start(); file sink (when enabled) keeps
    /// growing until plugin unload.
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

        // Always-on JSONL sink (bridge-format). Independent of in-memory ring.
        private readonly object _fileLock = new object();
        private StreamWriter? _fileSink;
        private string? _fileSinkPath;

        public bool Enabled => _enabled;
        public int Count => Volatile.Read(ref _count);
        public DateTime StartedAtUtc => _startedAtUtc;
        public string? FileSinkPath => _fileSinkPath;

        private SerialTrafficCapture() { }

        public void Start()
        {
            Clear();
            _startedAtUtc = DateTime.UtcNow;
            _enabled = true;
        }

        /// <summary>
        /// Open a JSONL sink at <paramref name="path"/>. Each subsequent Tx/Rx is
        /// written as a single bridge-compatible JSON line. Independent of
        /// <see cref="Enabled"/> — file sink writes whether the in-memory ring
        /// is on or off.
        /// </summary>
        public void StartFileSink(string path)
        {
            lock (_fileLock)
            {
                CloseFileSinkLocked();
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                _fileSink = new StreamWriter(new FileStream(
                    path, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    AutoFlush = true,
                };
                _fileSinkPath = path;
            }
        }

        public void StopFileSink()
        {
            lock (_fileLock) CloseFileSinkLocked();
        }

        private void CloseFileSinkLocked()
        {
            try { _fileSink?.Flush(); } catch { }
            try { _fileSink?.Dispose(); } catch { }
            _fileSink = null;
            _fileSinkPath = null;
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
            if (frame == null || frame.Length == 0) return;
            // File sink — always writes when open, even if ring is off.
            WriteFileSinkLine(dir, frame);

            if (!_enabled) return;
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

        private void WriteFileSinkLine(Direction dir, byte[] frame)
        {
            // Bridge-compatible JSONL: {"t":..., "dir":"h2b"|"b2h", "len":N, "ok":true,
            //                            "hex":"...", "grp":..., "dev":..., "payload":"..."}
            // Tx (host→device) = h2b; Rx (device→host) = b2h.
            // Frame layout: 7E [N] grp dev payload[N] cs. Skip when frame is too short.
            StreamWriter? sink;
            lock (_fileLock) sink = _fileSink;
            if (sink == null) return;

            var sb = new StringBuilder(frame.Length * 2 + 96);
            double t = (DateTime.UtcNow - _epoch).TotalSeconds;
            sb.Append("{\"t\":");
            sb.Append(t.ToString("F6", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"dir\":\"");
            sb.Append(dir == Direction.Tx ? "h2b" : "b2h");
            sb.Append("\",\"len\":");
            sb.Append(frame.Length);
            sb.Append(",\"ok\":true,\"hex\":\"");
            for (int i = 0; i < frame.Length; i++)
            {
                sb.Append(HexChar(frame[i] >> 4));
                sb.Append(HexChar(frame[i] & 0xF));
            }
            sb.Append('"');
            // grp/dev/payload extraction. Two shapes:
            //   * Tx: full wire frame `7E [N] grp dev payload[N-2] cs`
            //   * Rx: parsed message (FrameSplitter already stripped framing) —
            //     starts directly with `grp dev payload...`
            int grp = -1, dev = -1, payStart = -1, payEnd = -1;
            if (frame.Length >= 5 && frame[0] == 0x7E)
            {
                // MOZA wire framing: 7E [N] grp dev payload[N] cs.
                // N counts payload bytes only (excludes grp/dev/cs).
                int n = frame[1];
                if (frame.Length >= 4 + n + 1)
                {
                    grp = frame[2];
                    dev = frame[3];
                    payStart = 4;
                    payEnd = 4 + n;
                }
            }
            else if (frame.Length >= 2)
            {
                grp = frame[0];
                dev = frame[1];
                payStart = 2;
                payEnd = frame.Length;
            }
            if (grp >= 0 && dev >= 0 && payStart >= 0)
            {
                sb.Append(",\"grp\":");
                sb.Append(grp);
                sb.Append(",\"dev\":");
                sb.Append(dev);
                sb.Append(",\"payload\":\"");
                for (int i = payStart; i < payEnd; i++)
                {
                    sb.Append(HexChar(frame[i] >> 4));
                    sb.Append(HexChar(frame[i] & 0xF));
                }
                sb.Append('"');
            }
            sb.Append('}');
            try { lock (_fileLock) sink.WriteLine(sb.ToString()); }
            catch { /* sink may have been closed concurrently — silent drop */ }
        }

        private static readonly DateTime _epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

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

        private static char HexChar(int n) => (char)(n < 10 ? '0' + n : 'a' + (n - 10));
    }
}
