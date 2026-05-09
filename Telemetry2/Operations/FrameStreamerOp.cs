using System;
using System.Collections.Generic;
using GameReaderCommon;
using MozaPlugin.Telemetry;

namespace MozaPlugin.Telemetry2.Operations
{
    // Drives one TelemetryFrameBuilder per sub-tier. Reads flag bytes from the
    // TierDefNegotiator's ActiveSubscription and stamps them on each emitted frame.
    //
    // The frame transport is the legacy 7c:43 raw group format (NOT a session-chunked
    // stream): `7E [N] 43 17 7D 23 32 00 23 32 [flag] 20 [data] [chk]`. The existing
    // Telemetry/TelemetryFrameBuilder already knows this layout — FrameStreamerOp
    // wraps it rather than re-implementing.
    //
    // Phase 4 deliverable: thin coordinator that gives the host facade a clean API
    // (`Tick(snapshot)` → frames) decoupled from the negotiator's state machine. The
    // big merge of TelemetryEncoder + TelemetryBitWriter into Telemetry2/Protocol/FrameEncoder
    // is Phase 3 cleanup; here we delegate to the working code.
    public sealed class FrameStreamerOp
    {
        private readonly TelemetryFrameBuilder[] _tierBuilders;
        // Per-tier emit interval in ticks (System.DateTime.Ticks units). Each tier
        // emits at most once per its PackageLevel (in ms). Time-based gating instead
        // of counter-based because v2's Tick is driven by SimHub's DataUpdate rate
        // (variable, often 50-100 Hz) — counter-based would fire fast tiers way more
        // often than v1's fixed 33Hz timer + TickInterval gating.
        private readonly long[] _tierIntervalTicks;
        private readonly long[] _tierLastEmitTicks;
        private readonly TierDefNegotiator _negotiator;

        public FrameStreamerOp(MultiStreamProfile profile,
            TierDefNegotiator negotiator,
            Func<string, double>? propertyResolver,
            bool type02NConvention)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            _negotiator = negotiator ?? throw new ArgumentNullException(nameof(negotiator));

            _tierBuilders = new TelemetryFrameBuilder[profile.Tiers.Count];
            _tierIntervalTicks = new long[profile.Tiers.Count];
            _tierLastEmitTicks = new long[profile.Tiers.Count];

            // PackageLevel is the per-tier update period in ms — each tier emits at
            // its own rate, independent of a global base. Fall back to 30ms if a
            // tier has PackageLevel=0 (unspecified).
            for (int i = 0; i < profile.Tiers.Count; i++)
            {
                _tierBuilders[i] = new TelemetryFrameBuilder(profile.Tiers[i], propertyResolver, type02NConvention);
                int p = profile.Tiers[i].PackageLevel;
                int periodMs = (p > 0) ? p : 30;
                _tierIntervalTicks[i] = periodMs * TimeSpan.TicksPerMillisecond;
            }
        }

        // Build one round of frames — only the tiers whose interval has elapsed
        // since their last emission. Time-based to be invariant under variable Tick rate.
        public IList<byte[]> BuildFrames(StatusDataBase? gameData)
        {
            var sub = _negotiator.ActiveSubscription;
            if (sub.TierCount == 0) return Array.Empty<byte[]>();
            int n = Math.Min(sub.TierCount, _tierBuilders.Length);
            long now = DateTime.UtcNow.Ticks;
            var frames = new List<byte[]>(n);
            for (int i = 0; i < n; i++)
            {
                if (now - _tierLastEmitTicks[i] < _tierIntervalTicks[i]) continue;
                _tierLastEmitTicks[i] = now;
                byte flagByte = i < sub.FlagBytes.Count ? sub.FlagBytes[i] : (byte)(sub.FlagBase + i);
                frames.Add(_tierBuilders[i].BuildFrame(gameData, flagByte));
            }
            return frames;
        }

        // Test-pattern variant — same time-based gating.
        public IList<byte[]> BuildTestFrames()
        {
            var sub = _negotiator.ActiveSubscription;
            if (sub.TierCount == 0) return Array.Empty<byte[]>();
            int n = Math.Min(sub.TierCount, _tierBuilders.Length);
            long now = DateTime.UtcNow.Ticks;
            var frames = new List<byte[]>(n);
            for (int i = 0; i < n; i++)
            {
                if (now - _tierLastEmitTicks[i] < _tierIntervalTicks[i]) continue;
                _tierLastEmitTicks[i] = now;
                byte flagByte = i < sub.FlagBytes.Count ? sub.FlagBytes[i] : (byte)(sub.FlagBase + i);
                frames.Add(_tierBuilders[i].BuildTestFrame(flagByte));
            }
            return frames;
        }
    }
}
