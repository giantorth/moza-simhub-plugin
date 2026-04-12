using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MozaPlugin.Telemetry
{
    /// <summary>
    /// Captures sent frames and generates test patterns for user verification.
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

        /// <summary>
        /// Build synthetic game data that cycles values over time.
        /// Cycling values let users confirm each channel maps to the correct display element.
        /// </summary>
        /// <param name="frameCounter">Incremented each send tick.</param>
        public GameDataSnapshot BuildTestPattern(int frameCounter)
        {
            // Cycle over 200 frames (~10 sec at 20Hz)
            double t = (frameCounter % 200) / 200.0;

            return new GameDataSnapshot
            {
                SpeedKmh               = t * 200.0,
                Rpms                   = t * 8000.0,
                Gear                   = (int)(t * 6) + 1,      // 1–6
                Throttle               = t,                      // 0.0–1.0 (matches FromStatusData normalization)
                Brake                  = 1.0 - t,               // 0.0–1.0 (matches FromStatusData normalization)
                BestLapTimeSeconds     = 90 + t * 10,
                CurrentLapTimeSeconds  = t * 90,
                LastLapTimeSeconds     = 92 + t * 5,
                DeltaToSessionBest     = (t - 0.5) * 5.0,
                FuelPercent            = (1 - t) * 100.0,
                DrsEnabled             = frameCounter % 40 < 20 ? 1.0 : 0.0,
                ErsPercent             = t * 100.0,
                TyreWearFrontLeft      = t * 50.0,
                TyreWearFrontRight     = t * 40.0,
                TyreWearRearLeft       = t * 55.0,
                TyreWearRearRight      = t * 45.0,
            };
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
