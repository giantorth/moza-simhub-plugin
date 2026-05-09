# Wheel sess=0x09 internal timeout (2026-05-08)

The wheel maintains an internal ~10–14 second timeout on its sess=0x09
(configJson) dashboard-binding state. While that timer is running, the wheel
**silently ignores every host emission** on sess=0x09 — primes, open requests,
and anything else. The host's session frames are correctly written to the
wire and ack'd at the SerialStream layer, but the wheel's higher-level
state machine doesn't act on them. New `b2h 7c 00 09 81 ...` device-init
events do not fire until the timer expires.

## Symptoms

- After SimHub plugin reload (game switch — SimHub Stop+Start cycle the
  plugin in <1 s) the wheel's display stops driving telemetry on the new
  game's dashboard. Manual disable+enable of the plugin restores it ONLY
  IF the user waits ≥10 s between the two clicks.
- After a wheel-UI dashboard switch, the visual switch happens (kind=4
  echo at fc:00) but the new dashboard never renders driving telemetry —
  same root cause: tier-def reload happens but the wheel's sess=0x09
  binding state hasn't released its prior dashboard mapping.

## Wire-trace evidence

Across 8 cycles in 4 captures (2026-05-08), failing cycles had ≤8.4 s of
host silence between Stop and Start; working cycles had ≥9.5 s, with one
clean reset at 13.9 s. The decisive variable is **silence duration before
the next sess=0x09 prime**, not anything in the host frames themselves.

| Trace cycle | Host silence | b2h sess=0x09 OPEN fired? | Result |
|---|---|---|---|
| 150742 #1 | 100 ms | no | FAIL |
| 150742 #2 | 100 ms | no | FAIL |
| 150500 #1 | 100 ms | no | FAIL |
| 150500 #2 | 100 ms | no | FAIL |
| 144130 #2 | 100 ms | no | FAIL |
| 150608 #1 | 9.5 s | yes (34 ms after) | OK |
| 150742 #3 | 13.9 s | yes (53 ms after) | OK |
| 144130 #1 | cold-start | yes (51 ms after) | OK |

In every failing cycle, the host had emitted the sess=0x09 prime + open
request multiple times (the now-implemented retry would have fired 5+
rounds in those windows). The wheel never device-init'd. In every working
cycle the wheel's `b2h c3 71 7c 00 09 81 09 00 09 00 fd 02` arrived
within ~50 ms of the next prime.

## Why explicit closes don't help

Wheel-managed sess=0x09 stays alive across the silence — the wheel keeps
emitting its own b2h heartbeats on 0x09. The session itself is not the
issue; only its dashboard-binding state has the timeout. Closing 0x09
host-side either no-ops (best case) or severs the wheel's willingness to
re-engage at all (worst case, prior empirical evidence at
`Telemetry/TelemetrySender.cs CloseHostSessions` doc comment).

## Plugin response

Two changes in `Telemetry/TelemetrySender.cs`:

1. **`_lastStopUtcTicks` (static field) + `MinSilenceAfterStopMs = 11000`** —
   `Stop()` records `DateTime.UtcNow.Ticks` at completion. `StartInner`,
   on a ThreadPool thread, computes elapsed and `Thread.Sleep`s the
   remainder if less than 11 s have passed. Static so it survives
   plugin instance recycle within the same SimHub process — game switches
   reload the plugin (new instance) but the wheel-side timeout is
   wall-clock and applies across both instances. Cold-start (first run
   of the process) skips the gate via `if (_lastStopUtcTicks != 0)`.

2. **`TickRetryS09IfNotEstablished`** — secondary defense: even when the
   wheel's timeout has cleared, a single dropped chunk under Wine
   SerialPort R/W contention can stall the prime+open-request emission.
   The retry helper re-emits `SendSessionPrime(0x09, ...)` +
   `SendConfigJsonOpenRequest(0x09, ...)` at 1 s intervals (10 round
   budget) until `_sessions.GetOrCreate(0x09).DeviceInitiated == true`.
   Guarded so steady-state and post-switch sessions are untouched.

## Interaction with dashboard-switch driving

`OnDashboardSwitched` (the UI knob path) and `SwitchToProfile` (auto-test)
now route through `RestartForSwitch()` which fires Stop+Start. The
silence gate above ensures the 11 s wait happens automatically inside
`StartInner`. Total switch latency is ~12.5 s (300 ms drain + 11 s
silence + ~1 s preamble) — slow but reliable. Renegotiate-in-place is no
longer used for switches.

## Files

- `Telemetry/TelemetrySender.cs`
  - `CloseHostSessions` (closes 01/02/03 only; doc explains why 04..0a
    stay alone)
  - `Stop()` records `_lastStopUtcTicks` at end
  - `StartInner` enforces `MinSilenceAfterStopMs` gate
  - `RestartForSwitch` for dashboard-switch pipeline cycle
  - `TickRetryS09IfNotEstablished` for sess=0x09 establishment retry
- `MozaPlugin.cs`
  - `OnDashboardSwitched` calls `ApplyTelemetrySettings()` +
    `sender.RestartForSwitch()`
