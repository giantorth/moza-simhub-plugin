using System;
using System.Collections.Generic;
using MozaPlugin.Telemetry2.Wire;
using ISessionConsumer = MozaPlugin.Telemetry2.Sessions.ISessionConsumer;

namespace MozaPlugin.Telemetry2.Operations
{
    // Inbound consumer for wheel-pushed catalog (URL→idx) records on b2h sessions.
    // The wheel announces its channel catalog via tag=0x04 records that arrive on
    // session 0x01 OR 0x02 depending on firmware (V0 URL-subscription post-2026-04
    // CSP firmware uses 0x01; V2-compact uses 0x02 — per Telemetry/TelemetrySender.cs:2330-2334).
    //
    // The consumer accumulates each session's chunk payloads (CRC-stripped) into a
    // running buffer, scans for tag=0x04 URL records via WheelCatalogParser, builds
    // a 1-based URL list, and fires CatalogChanged whenever a new URL appears.
    public sealed class CatalogConsumer : ISessionConsumer
    {
        private readonly object _lock = new object();
        private readonly Dictionary<byte, List<byte>> _bufferPerSession = new Dictionary<byte, List<byte>>();
        private readonly List<string?> _sparseCatalog = new List<string?>();

        public event EventHandler<IReadOnlyList<string>>? CatalogChanged;

        // Sessions this consumer claims. Both 0x01 and 0x02 carry catalog announcements
        // depending on firmware era; consumer scans both.
        public static readonly byte[] CatalogSessions = new[] { (byte)0x01, (byte)0x02 };

        public IReadOnlyList<string> CurrentCatalog
        {
            get { lock (_lock) return WheelCatalogParser.CompactByIndex(_sparseCatalog); }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _bufferPerSession.Clear();
                _sparseCatalog.Clear();
            }
        }

        public void OnData(byte session, int seq, byte[] payload)
        {
            if (payload == null || payload.Length == 0) return;
            // Cross-session scan: real wheel 2026-05-04 sent the catalog on b2h
            // session 0x00, not 0x01 or 0x02 (PitHouse 2026-05-03 captures used 0x02).
            // The parser is tolerant — it only reacts to tag=0x04 byte sequences and
            // ignores everything else — so feeding all sessions is safe.

            byte[] netPayload = StripCrcTrailerIfPresent(payload);

            bool changed = false;
            lock (_lock)
            {
                if (!_bufferPerSession.TryGetValue(session, out var buf))
                {
                    buf = new List<byte>(256);
                    _bufferPerSession[session] = buf;
                }
                buf.AddRange(netPayload);

                // Cap buffer growth — catalogs typically resolve well under 8 KB.
                // Clear entirely on overflow rather than partial trim, which could
                // split a TLV record mid-tag and corrupt subsequent parsing.
                const int Cap = 64 * 1024;
                if (buf.Count > Cap)
                    buf.Clear();

                var fresh = WheelCatalogParser.Parse(buf.ToArray());
                if (MergeFresh(fresh)) changed = true;
            }
            if (changed)
            {
                IReadOnlyList<string> snapshot;
                lock (_lock) snapshot = WheelCatalogParser.CompactByIndex(_sparseCatalog);
                CatalogChanged?.Invoke(this, snapshot);
            }
        }

        public void OnAck(byte session, int ackSeq) { }
        public void OnOpen(byte session, int openSeq) { }
        public void OnClose(byte session, int ackSeq) { }

        // Returns true if any URL was newly added or changed.
        private bool MergeFresh(IReadOnlyList<string?> fresh)
        {
            bool any = false;
            for (int idx0 = 0; idx0 < fresh.Count; idx0++)
            {
                string? url = fresh[idx0];
                if (string.IsNullOrEmpty(url)) continue;
                while (_sparseCatalog.Count <= idx0) _sparseCatalog.Add(null);
                if (!string.Equals(_sparseCatalog[idx0], url, StringComparison.OrdinalIgnoreCase))
                {
                    _sparseCatalog[idx0] = url;
                    any = true;
                }
            }
            return any;
        }

        // Inbound chunk payloads from raw frames carry a 4-byte CRC32 trailer.
        // ChunkReassembler validates + strips it; consumers receiving "validated"
        // payloads will already be CRC-stripped. For raw routes that bypass the
        // reassembler, fall back to trailing-4-byte strip.
        private static byte[] StripCrcTrailerIfPresent(byte[] payload)
        {
            // Heuristic: if last 4 bytes' CRC over payload[0..-4] matches, strip.
            if (payload.Length < 5) return payload;
            uint wire = (uint)(payload[payload.Length - 4]
                              | (payload[payload.Length - 3] << 8)
                              | (payload[payload.Length - 2] << 16)
                              | (payload[payload.Length - 1] << 24));
            uint actual = Crc32.Compute(payload, 0, payload.Length - 4);
            if (actual == wire)
            {
                byte[] trimmed = new byte[payload.Length - 4];
                Array.Copy(payload, 0, trimmed, 0, trimmed.Length);
                return trimmed;
            }
            return payload;
        }
    }
}
