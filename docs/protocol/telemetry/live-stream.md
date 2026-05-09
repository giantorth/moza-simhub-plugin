## Live telemetry stream (group 0x43, device 0x17, cmd `[0x7D, 0x23]`)

Primary live data stream from Pithouse to wheel/dash. Sent ~17–20×/s.

### Frame structure

```
7E [N] 43 17  7D 23  [6-byte header]  [live data]  [checksum]
```

**Header** (6 bytes, after cmd ID):

| Byte | Value | Notes |
|------|-------|-------|
| 0–3 | `32 00 23 32` | Constant across all captures |
| 4 | varies | **Flag byte** — determines payload type (see below) |
| 5 | `0x20` | Constant across all captures |

### Flag byte and multi-stream architecture

Pit House sends telemetry as **three concurrent streams** using different flag bytes, one per `package_level` tier defined in `GameConfigs/Telemetry.json`. Each stream carries channels assigned to its tier, bit-packed alphabetically by URL suffix. See [`tiers.md`](tiers.md) for the canonical tier-concept reference (assignment, cadence, end-to-end channel example).

| Flag offset | `package_level` | Update rate | Content |
|-------------|----------------|-------------|---------|
| base (e.g. `0x0a`, `0x13`) | 30 | ~30 ms | Channels with `package_level: 30` |
| base+1 | 500 | ~500 ms | Channels with `package_level: 500` |
| base+2 | 2000 | ~2000 ms | Channels with `package_level: 2000` |

`package_level` is authoritative routing key — channel's tier fixed in `Telemetry.json`, independent of active dashboard. If tier has no active channels, frame sent as 2-byte stub `[flag][0x20]`. Flag value is monotonic counter assigned per connection; base+1 and base+2 always exactly one and two above base.

### Flag byte values across captures

Wheel accepts flags at 0x00, 0x02, 0x07, 0x0a, 0x13 — any value works as long as tier definition and telemetry frames agree. Exact relationship between enable entry offsets and tier flag bytes is **not fully understood**. Plugin exposes `FlagByteMode` (0=zero-based, 1=session-port-based, 2=two-batch) for empirical testing.

**Pithouse flag byte assignment:**

- 2026-04-12 captures (older firmware): Pithouse uses 0-based flag bytes regardless of session port. Tier definitions use flags 0x00, 0x01, 0x02 and first telemetry frame uses flag=0x00 — even though telemetry session was on port 0x02. Pithouse starts with flag=0x00 (fastest tier).
- **2026-04-29 capture** (R5+W17 Type02 firmware, Nebula in-game): tier definitions still at flags 0x00/0x01/0x02 broadcasting **the same channels at three rates**, but value frames emit at **flag=0x02 only**. Wheel binds widgets to flag=2 (the highest flag); frames at flags 0/1 don't update widgets. Plugin verified non-rendering on flag=0 → rendering when frames moved to flag=2 (2026-04-29 live test).

The shift between eras: older firmware = flag=0 fast tier; Type02 firmware = flag=2 fast tier. Pick by firmware era, not by absolute byte value.

Observed flag bytes (from raw JSON):

| Capture | Flag |
|---------|------|
| `moza-startup.json` | 0x02 — first port after power-on |
| `burn-tyres.json` | 0x0a — later connection |
| `0-100redline-0-main-dash.json` | 0x13 — even later connection |

### Example: F1 dashboard tier layouts

Level-30 channels (base frame), alphabetical, verified from capture (Gear at bit 79):

| Bits | Channel | Compression | Width |
|------|---------|-------------|-------|
| 0–9 | Brake | `float_001` | 10 |
| 10–41 | CurrentLapTime | `float` | 32 |
| 42 | DrsState | `bool` | 1 |
| 43–46 | ErsState | `uint3` | 4 |
| 47–78 | GAP | `float` | 32 |
| 79–83 | Gear | `int30` | 5 |
| 84–99 | Rpm | `uint16_t` | 16 |
| 100–115 | SpeedKmh | `float_6000_1` | 16 |
| 116–125 | Throttle | `float_001` | 10 |
| 126–127 | *(padding)* | | 2 |

Total payload bytes = `ceil(sum_of_channel_bit_widths / 8)`. 128 bits = 16 bytes.

Level-2000 frame (base+2) — 6 channels, 104 bits = 13 bytes exactly:

| Bits | Channel | Compression | Width |
|------|---------|-------------|-------|
| 0–31 | BestLapTime | `float` | 32 |
| 32–63 | LastLapTime | `float` | 32 |
| 64–73 | TyreWearFrontLeft | `percent_1` | 10 |
| 74–83 | TyreWearFrontRight | `percent_1` | 10 |
| 84–93 | TyreWearRearLeft | `percent_1` | 10 |
| 94–103 | TyreWearRearRight | `percent_1` | 10 |

All 3 F1 tiers verified byte-size-match vs Pithouse: Level 30 = 16B, Level 500 (FuelRemainder only) = 2B, Level 2000 = 13B.

### Data verification (2026-04-12)

Byte-level verification complete:
- Header `7E [N] 43 17 7D 23 32 00 23 32 [flag] 20 [data] [checksum]` — constant bytes, N, checksum match Pithouse exactly.
- LSB-first `TelemetryBitWriter` correct. Case-insensitive URL sort matches Pithouse.
- Encoding formulas verified against capture: `float_001` (×1000), `percent_1` (×10), `uint16_t` (direct), `float_6000_1` (×10), `int30` (5-bit, -1→31), `float` (IEEE 754), `bool` (0/1).
