using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Captures sent frames for user verification and export.
    /// </summary>
    public class TelemetryDiagnostics
    {
        private const int MaxLogEntries = 100;
        private readonly Queue<TelemetryLogEntry> _sentFrames = new Queue<TelemetryLogEntry>();
        private readonly object _lock = new object();

        /// <summary>Records a frame that was sent.</summary>
        public void RecordFrame(byte[] frame)
        {
            lock (_lock)
            {
                while (_sentFrames.Count >= MaxLogEntries)
                    _sentFrames.Dequeue();
                _sentFrames.Enqueue(new TelemetryLogEntry(frame));
            }
        }

        /// <summary>Returns a snapshot of the recent frame log.</summary>
        public TelemetryLogEntry[] GetLog()
        {
            lock (_lock)
                return _sentFrames.ToArray();
        }

        /// <summary>Export the frame log to a text file.</summary>
        public void ExportLog(string path)
        {
            TelemetryLogEntry[] entries;
            lock (_lock)
                entries = _sentFrames.ToArray();

            using var writer = new StreamWriter(path, false, Encoding.UTF8);
            writer.WriteLine($"# Moza Telemetry Frame Log — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            writer.WriteLine($"# {entries.Length} entries");
            writer.WriteLine();

            foreach (var entry in entries)
                writer.WriteLine($"[{entry.Timestamp:HH:mm:ss.fff}] {entry.FrameHex}");
        }
    }

    public class TelemetryLogEntry
    {
        public DateTime Timestamp { get; }
        public byte[] Frame { get; }

        public TelemetryLogEntry(byte[] frame)
        {
            Timestamp = DateTime.UtcNow;
            Frame = frame;
        }

        public string FrameHex => BitConverter.ToString(Frame).Replace("-", " ").ToLowerInvariant();
    }
}
