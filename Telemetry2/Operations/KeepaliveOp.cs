using MozaPlugin.Telemetry2.Wire;

namespace MozaPlugin.Telemetry2.Operations
{
    // Periodic heartbeat on h2b session 0x02. Per Phase 0 (init-sequence findings):
    //   kind=14 — constant value 100 (=0x64), every ~2s
    //   kind=15 — variable u32 (1, 2, 3, 24 observed); semantics partially open
    //
    // Initial implementation emits kind=14=100 + kind=15=value where value defaults to
    // 24 (the most common observed value). The host's tick loop calls Tick() which
    // returns the FfRecords to send when the cadence elapses, or empty otherwise.
    public sealed class KeepaliveOp
    {
        private readonly long _intervalTicks;
        private long _lastTicks;
        private bool _initialised;

        public uint Heartbeat14Value { get; set; } = 100;
        public uint Heartbeat15Value { get; set; } = 24;

        // intervalMs: cadence between heartbeat pairs. PitHouse uses ~2000ms.
        public KeepaliveOp(int intervalMs = 2000)
        {
            _intervalTicks = intervalMs * System.TimeSpan.TicksPerMillisecond;
        }

        // Call from the host's tick loop with a monotonic timestamp (e.g. Stopwatch
        // ticks or DateTime.UtcNow.Ticks). Returns records to emit, or empty array if
        // not yet due. The first call after construction or Reset() establishes the
        // baseline timestamp without emitting; subsequent calls after the interval
        // elapses emit the heartbeat pair and reset the baseline.
        public FfRecord[] Tick(long nowTicks)
        {
            if (!_initialised)
            {
                _lastTicks = nowTicks;
                _initialised = true;
                return System.Array.Empty<FfRecord>();
            }
            if (nowTicks - _lastTicks < _intervalTicks)
                return System.Array.Empty<FfRecord>();
            _lastTicks = nowTicks;
            return new[]
            {
                FfRecord.Heartbeat14(Heartbeat14Value),
                FfRecord.Heartbeat15(Heartbeat15Value),
            };
        }

        public void Reset()
        {
            _initialised = false;
            _lastTicks = 0;
        }
    }
}
