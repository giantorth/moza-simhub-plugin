using System;
using System.Collections.Generic;
using MozaPlugin.Telemetry2.Protocol;
using MozaPlugin.Telemetry2.Wire;

namespace MozaPlugin.Telemetry2.Operations
{
    // Pure state machine that emits tier-def TLV byte streams matching PitHouse output.
    // Owns the cumulative session-wide state required by Phase 0 byte-exact rules:
    //   - _nextFlagBase: monotonic flag counter (int), advances by tier count per emission; cast to byte at wire time
    //   - _prevSectionFlags: the flag bytes of the last-emitted section (becomes ENABLEs of next section)
    //   - _maxChannelIdx: cumulative max channel index ever referenced anywhere this session
    //   - _preambleSent: gates the one-shot PROTO_VER + FLAG_BASE preamble at session start
    //
    // Per-broadcast emission model (matches PitHouse captures):
    //   PitHouse emits one section per broadcast cycle, not all broadcasts at once.
    //   For a 3-sub-tier dashboard with broadcasts=5:
    //     E0: preamble + 3 tiers (broadcast 1) + END
    //     E1: ENABLE prev + 3 tiers (broadcast 2) + END
    //     ...
    //     E4: ENABLE prev + 3 tiers (broadcast 5) + END
    //   Each NextEmission() returns one broadcast cycle's section. The host calls
    //   NextEmission() repeatedly; _broadcastsRemaining tracks how many are left.
    //
    // Inputs (set via methods):
    //   SetCatalog(urlsByIndex)       — wheel-pushed catalog: position i → URL at index i+1
    //   SetActiveDashboard(spec)      — current dashboard's tiers and channel descriptors
    //   Reset()                       — full session reset (disconnect)
    //
    // Output:
    //   NextEmission() → byte[] or null. Returns the bytes for one tier-def section
    //   when a fresh emission is pending; null otherwise. The host calls this on its
    //   tick and pushes the result through the SessionEndpoint for session 0x01.
    public sealed class TierDefNegotiator
    {
        // One sub-tier descriptor as the negotiator sees it: a list of (url, comp, bw)
        // channels. The negotiator resolves URL→index against the catalog at emission time.
        public sealed class SubTier
        {
            public IReadOnlyList<ChannelSpec> Channels { get; }
            public SubTier(IReadOnlyList<ChannelSpec> channels)
            {
                Channels = channels ?? throw new ArgumentNullException(nameof(channels));
            }
        }

        public readonly struct ChannelSpec
        {
            public string Url { get; }
            public string CompressionName { get; }
            public int BitWidth { get; }
            public ChannelSpec(string url, string compressionName, int bitWidth)
            {
                Url = url;
                CompressionName = compressionName;
                BitWidth = bitWidth;
            }
        }

        // The active dashboard shape: an ordered list of sub-tiers. Each NextEmission
        // call emits one section containing all sub-tiers in order, advancing flag bases.
        public sealed class DashboardSpec
        {
            public string Name { get; }
            public IReadOnlyList<SubTier> SubTiers { get; }
            public DashboardSpec(string name, IReadOnlyList<SubTier> subTiers)
            {
                Name = name ?? "";
                SubTiers = subTiers ?? throw new ArgumentNullException(nameof(subTiers));
            }
        }

        private readonly object _lock = new object();
        private int _nextFlagBase;
        private List<byte> _prevSectionFlags = new List<byte>();
        private uint _maxChannelIdx;
        private bool _preambleSent;
        private DashboardSpec? _activeDashboard;
        private Dictionary<string, uint> _urlToIndex = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        private bool _pendingEmission;
        private int _broadcastsRemaining;

        // Subscription snapshot the FrameStreamer reads to stamp value-frame flag bytes.
        // Updated atomically when an emission is produced.
        private SubscriptionSnapshot _activeSubscription = SubscriptionSnapshot.Empty;
        public SubscriptionSnapshot ActiveSubscription
        {
            get { lock (_lock) return _activeSubscription; }
        }

        // Diagnostics — expose internal gate state without taking action.
        public bool PendingEmission { get { lock (_lock) return _pendingEmission; } }
        public bool HasDashboard { get { lock (_lock) return _activeDashboard != null; } }
        public int CatalogSize { get { lock (_lock) return _urlToIndex.Count; } }

        // Reset session-cumulative state (flag base, prev-section flags, max channel idx,
        // preamble-sent gate). Preserves config (active dashboard + catalog) so a Start
        // → Stop → Start cycle picks up the same dashboard with a fresh handshake. If
        // both inputs are still set, queues a fresh emission.
        public void Reset()
        {
            lock (_lock)
            {
                _nextFlagBase = 0;
                _prevSectionFlags = new List<byte>();
                _maxChannelIdx = 0;
                _preambleSent = false;
                _broadcastsRemaining = 0;
                _activeSubscription = SubscriptionSnapshot.Empty;
                _pendingEmission = _activeDashboard != null && _urlToIndex.Count > 0;
                if (_pendingEmission)
                    _broadcastsRemaining = ComputeBroadcastCount(_activeDashboard!);
            }
        }

        // Full wipe — clear config too. Use on full disconnect.
        public void ResetAll()
        {
            lock (_lock)
            {
                _nextFlagBase = 0;
                _prevSectionFlags = new List<byte>();
                _maxChannelIdx = 0;
                _preambleSent = false;
                _broadcastsRemaining = 0;
                _activeDashboard = null;
                _urlToIndex.Clear();
                _pendingEmission = false;
                _activeSubscription = SubscriptionSnapshot.Empty;
            }
        }

        // Update wheel catalog. Position i in the list → channel index i+1 (1-based per
        // PitHouse convention). Empty / whitespace entries are skipped.
        //
        // Preamble lifecycle (D5 fix, anchored on moza-wire-20260505-114341.jsonl):
        //   PitHouse rule §1: preamble (PROTO_VER + FLAG_BASE) is one-shot per session.
        //   The previous behavior reset _preambleSent on every catalog change, producing
        //   3× PROTO_VER at cold-start (offsets 0/131/262 in that wire trace) when the
        //   captured-catalog fallback flipped to a real announcement and possibly back.
        //   Now we ONLY rebase cumulative state when the catalog goes 0 → non-empty
        //   (i.e. first time we have any URL→idx data). Subsequent re-announcements of
        //   the same or a different catalog do NOT re-emit the preamble or rebase flag
        //   counters; PitHouse handles catalog evolution silently within an active session.
        public void SetCatalog(IReadOnlyList<string> urlsByIndex)
        {
            lock (_lock)
            {
                var newMap = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
                if (urlsByIndex != null)
                {
                    for (int i = 0; i < urlsByIndex.Count; i++)
                    {
                        string url = urlsByIndex[i];
                        if (!string.IsNullOrWhiteSpace(url))
                            newMap[url] = (uint)(i + 1);
                    }
                }
                bool wasEmpty = _urlToIndex.Count == 0;
                bool nowPopulated = newMap.Count > 0;
                _urlToIndex = newMap;
                if (wasEmpty && nowPopulated)
                {
                    _nextFlagBase = 0;
                    _prevSectionFlags = new List<byte>();
                    _maxChannelIdx = 0;
                    _preambleSent = false;
                    _broadcastsRemaining = 0;
                    _activeSubscription = SubscriptionSnapshot.Empty;
                    if (_activeDashboard != null)
                    {
                        _pendingEmission = true;
                        _broadcastsRemaining = ComputeBroadcastCount(_activeDashboard);
                    }
                }
                // Don't re-queue on incremental updates (non-empty → non-empty).
                // Dashboard switches queue their own emission via SetActiveDashboard.
            }
        }

        // Set / replace the active dashboard. Whether this is the initial connect or a
        // mid-session switch, behavior is identical: queue an emission. The actual bytes
        // produced depend on cumulative state, so initial-vs-switch is invisible to the wire.
        public void SetActiveDashboard(DashboardSpec dashboard)
        {
            lock (_lock)
            {
                _activeDashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
                _broadcastsRemaining = ComputeBroadcastCount(dashboard);
                _pendingEmission = true;
            }
        }

        // Broadcast count per PitHouse captures (verified against 112940/113616/115840):
        //   single sub-tier  → 3 broadcasts
        //   multiple sub-tiers → max(4, subCount + 1)
        // The host fires NextEmission() once per broadcast cycle; each produces one section.
        private static int ComputeBroadcastCount(DashboardSpec spec)
        {
            int n = spec.SubTiers.Count;
            if (n <= 0) return 0;
            return n == 1 ? 3 : Math.Max(4, n + 1);
        }

        // Returns one fresh tier-def emission's bytes, or null if nothing pending or inputs
        // not yet available. The host calls this on every tick; null returns are normal.
        public byte[]? NextEmission()
        {
            lock (_lock)
            {
                if (!_pendingEmission) return null;
                if (_activeDashboard == null) return null;
                if (_urlToIndex.Count == 0) return null;
                if (_activeDashboard.SubTiers.Count == 0) return null;

                // Build one TierSpec per sub-tier with advancing flag bytes.
                var tiers = new TierSpec[_activeDashboard.SubTiers.Count];
                var newSectionFlags = new List<byte>(_activeDashboard.SubTiers.Count);
                uint sectionMaxIdx = _maxChannelIdx;
                for (int i = 0; i < _activeDashboard.SubTiers.Count; i++)
                {
                    var sub = _activeDashboard.SubTiers[i];
                    byte flag = (byte)(_nextFlagBase + i);
                    var records = new ChannelRecord[sub.Channels.Count];
                    for (int c = 0; c < sub.Channels.Count; c++)
                    {
                        var ch = sub.Channels[c];
                        uint idx = ResolveIndex(ch.Url);
                        uint compCode = CompressionTable.GetByName(ch.CompressionName).Code;
                        records[c] = new ChannelRecord(idx, compCode, (uint)ch.BitWidth);
                        if (idx > sectionMaxIdx) sectionMaxIdx = idx;
                    }
                    tiers[i] = new TierSpec(flag, records);
                    newSectionFlags.Add(flag);
                }

                // First emission of the session = preamble + section with empty prev list,
                // END = 0 (Phase 0 rule: section 1 of session uses END = 0; thereafter
                // END = cumulative max idx).
                IReadOnlyList<byte> prev;
                uint endValue;
                bool emitPreamble;
                if (!_preambleSent)
                {
                    emitPreamble = true;
                    prev = Array.Empty<byte>();
                    endValue = 0;
                }
                else
                {
                    emitPreamble = false;
                    prev = _prevSectionFlags;
                    endValue = sectionMaxIdx;
                }

                byte[] sectionBytes = TierDefBuilder.BuildSection(prev, tiers, endValue);
                byte[] result;
                if (emitPreamble)
                {
                    byte[] preamble = TierDefBuilder.BuildPreamble();
                    result = new byte[preamble.Length + sectionBytes.Length];
                    Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
                    Buffer.BlockCopy(sectionBytes, 0, result, preamble.Length, sectionBytes.Length);
                }
                else
                {
                    result = sectionBytes;
                }

                // Commit state for the next broadcast cycle.
                _preambleSent = true;
                _prevSectionFlags = newSectionFlags;
                _maxChannelIdx = sectionMaxIdx;
                _nextFlagBase += _activeDashboard.SubTiers.Count;
                _broadcastsRemaining--;
                _pendingEmission = _broadcastsRemaining > 0;

                _activeSubscription = new SubscriptionSnapshot(
                    flagBase: newSectionFlags[0],
                    tierCount: newSectionFlags.Count,
                    profileName: _activeDashboard.Name,
                    flagBytes: newSectionFlags.ToArray());

                return result;
            }
        }

        private uint ResolveIndex(string url)
        {
            if (_urlToIndex.TryGetValue(url, out uint idx)) return idx;
            // Channel URL not in wheel catalog. Use 0 as sentinel — the wheel will treat
            // this as an unknown channel and ignore it. Phase 0 captures show every URL
            // resolves; mismatches mean the dashboard or catalog is stale on one side.
            return 0;
        }
    }

    // Snapshot of the current subscription published when the negotiator emits. The
    // FrameStreamerOp reads this to stamp `flagByte = FlagBytes[i]` on per-tier value frames.
    public readonly struct SubscriptionSnapshot
    {
        public byte FlagBase { get; }
        public int TierCount { get; }
        public string ProfileName { get; }
        public IReadOnlyList<byte> FlagBytes { get; }

        public SubscriptionSnapshot(byte flagBase, int tierCount, string profileName, IReadOnlyList<byte> flagBytes)
        {
            FlagBase = flagBase;
            TierCount = tierCount;
            ProfileName = profileName;
            FlagBytes = flagBytes;
        }

        public static readonly SubscriptionSnapshot Empty =
            new SubscriptionSnapshot(0, 0, "", Array.Empty<byte>());
    }
}
