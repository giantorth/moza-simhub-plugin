## First-time wheel catalog read (2026-04-28)

Captured live on `bridge.py` Linux passthrough between PitHouse (Windows) and an R5 base with a freshly-attached wheel (dev `0x17`). Documents the **one-shot configuration sweep** PitHouse runs the first time it sees a new wheel — distinct from the periodic `0x40 0x17` polling already noted in [`../startup-timeline.md`](../startup-timeline.md).

> **Source:** `sim/logs/bridge-20260428-155843.jsonl`, frames between t+1.10 s (first identity probe) and t+2.26 s (last one-shot reply). 83 unique `0x40 0x17` reads issued in ~750 ms.

### Phase ordering

| Phase | Window (rel. to bridge start) | Frames | Purpose |
|-------|-------------------------------|--------|---------|
| **A — identity probe** | t+1.105 s … t+1.357 s | 12 | Already documented: groups `0x09 0x04 0x06 0x02 0x05 0x07 0x0F 0x11 0x08 0x10`, plus tail `08:02` (hw-revision sub-2) and `10:01` (serial-b) |
| **B — catalog read** | t+1.508 s … t+2.260 s | 83 unique reads | Settings + LED state snapshot. *Subject of this finding.* |
| **C — steady-state poll** | t+2.3 s onward | repeats subset of B at ~1 Hz | Existing "Config burst" / `0x40/28:02` polling. Identity, `0x0C` device-info, and the per-LED `0x1F` sweep do **not** repeat. |

### Phase B contents (one-shot, 750 ms)

All requests are `0x40 0x17 [cmd-prefix] [zero-padded value template] [chk]`. The base mirrors via `0xC0 0x71 [cmd-echo] [real-value]`. Read templates pad the payload to the **width of the expected response** — e.g. a 3-byte RGB read sends three `0x00` bytes after the command prefix.

**Simple reads (no value template):**

| Cmd | Doc'd as | Wheel reply (this capture) |
|-----|----------|----------------------------|
| `0c` | device-info (12 B) | `03 03 03 1a 03 c9 04 13 06 82 06 82` (different from CSP example) |
| `13` | key-combination (4 B) | `ff ff ff ff` (unset) |

**Sub-indexed reads (1- or 2-byte cmd, padded to value width):**

| Cmd prefix | Doc'd as | Reads issued |
|------------|----------|--------------|
| `03 00` | paddles-mode | 1 |
| `05 00` | stick-mode | 1 |
| `09 00` | clutch-point | 1 |
| `0a 00` | knob-mode | 1 |
| `0b 00` | paddle-adaptive-mode | 1 |
| `0d 00` | paddle-button-mode | 1 |
| `20 00` | sleep-mode | 1 |
| `21 00 00` | idle-timeout (sleep-timeout, 2 B) | 1 |
| `22 01 00 00` | sleep-breath-interval sub=1 (2 B) | 1 |
| `24 ff 01 ff 00 00 00` | sleep-breath-color (3 B RGB) | 1 |
| `29 00` | open question (purpose unknown — see [`../open-questions.md`](../open-questions.md) "Group 0x28 / 0x29 purpose"). Replies `29 04` (1 B) — new datapoint for wheel context | 1 |

**Per-LED-group reads** (G = LED group ID 0–4 from [`../leds/wheel-groups-0x3F-0x40.md`](../leds/wheel-groups-0x3F-0x40.md)):

| Cmd prefix | Doc'd as | Range polled |
|------------|----------|--------------|
| `1b [G] ff 00 00` | group-brightness | G = 0, 1 |
| `1c [G] 00` | group-normal-mode | G = 0, 1 |
| `1d [G] 00` | group-standby-mode | G = 0, 1 |
| `1e [G] [N] 00 00` | group-standby-interval | G = 0 N = 2–6; G = 1 N = 2–5 |
| `1f [G] ff [N] 00 00 00` | group-led-color (RGB per LED) | G = 0 N = 0–15 (16 LEDs); G = 1 N = 0–11 (12 LEDs) |
| `27 [G] [role] 00 00 00` | led-group-color (idle/active RGB) | G = 0–3, role = 0, 1 |
| `28 00 00`, `28 01 00`, `28 02 [00\|01] 00` | multi-function-switch | N = 0, 1, 2 (sub = 0,1 for N = 2) |
| `2a [N] 00` | rotary-signal-mode | N = 0–3 |

The `1F` LED-state sweep returns the **stored static color** of every LED. In this capture all 28 LEDs replied with the same RGB `56 f7 fc` (cyan) — i.e. the wheel was at its factory-uniform state.

### Why this matters

1. **Cache-on-detect, not poll-on-demand.** PitHouse reads the entire static LED catalog (28 frames for `1F`) once per wheel-detect event and never repeats it. Sims and bridges that drop frames during the first ~2 s after enumeration will leave PitHouse with a half-populated wheel-config UI until the user disconnects/reconnects.
2. **Enumeration discovers LED count.** The host sweeps `1F [G] FF [N]` from `N=00` upwards; the actual LED count per group appears to be inferred from where the wheel either errors or returns a "no such index" reply. In this capture the host stopped at 16 (G=0) and 12 (G=1) — values consistent with the physical wheel and below the `wheel-groups-0x3F-0x40.md` documented maxes (25 / 16). A sim must therefore reply meaningfully to all `1F` indices it claims to support; gaps will truncate the discovered count.
3. **`29 00` adds a wheel-context datapoint.** [`../open-questions.md`](../open-questions.md) records group `0x29` to base as observed-but-unknown (values 1100). Wheel-side dev `0x17` returns `29 04`. Confirms `0x29` is queried wheel-side too, not just base-side; refines the open question.
4. **Read-template width matters.** A `0x40` read is rejected (or the response shape changes) if the zero-padded template length does not match the value width. PitHouse always pads to value width — sims emulating the base must not assume a fixed read-payload size for group `0x40`.
5. **`0x0C` device-info burst, not session-long.** Existing note in [`../devices/wheel-0x17.md`](../devices/wheel-0x17.md) says "PitHouse polls ~6×/session". This capture saw exactly 3 reads, all bunched within the first ~600 ms after wheel detect (~3 Hz), then no further reads for the rest of the session. The "~6×" earlier observation may have included a second wheel-detect event in that capture.

### Cross-references

- [`../startup-timeline.md`](../startup-timeline.md) — phase ordering at the session level
- [`../identity/wheel-probe-sequence.md`](../identity/wheel-probe-sequence.md) — Phase A details
- [`../devices/wheel-0x17.md`](../devices/wheel-0x17.md) — full command table; needs `29` row added
- [`../leds/wheel-groups-0x3F-0x40.md`](../leds/wheel-groups-0x3F-0x40.md) — LED group architecture
- [`../settings/wheel-0x17.md`](../settings/wheel-0x17.md) — wheel settings encoding
