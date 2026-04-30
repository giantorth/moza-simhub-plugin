# 2026-04-29 — session `0x01` host→wheel property push (`ff` records)

PitHouse pushes per-property setting updates (dashboard brightness, display
standby timeout, etc.) to the wheel-integrated dashboard sub-device via
**SerialStream session `0x01` data chunks** carrying an `ff`-tagged record.
This is **distinct from** the wheel-settings group `0x3F`/`0x40` (dev `0x17`)
and the standalone-MDD group `0x32`/`0x33` (dev `0x14`) writes — same
properties (e.g. RPM brightness) may exist in multiple paths but the
PitHouse Settings UI sliders for the integrated dashboard send only the
session-0x01 push. Reverse-engineered from CSP wheel sim captures while
moving brightness and display-standby sliders.

## Outer wire frame

Standard MOZA frame, group `0x43` to dev `0x17`, carrying a SerialStream
chunk per [`../sessions/chunk-format.md`](../sessions/chunk-format.md):

```
7e <LEN> 43 17  7c 00  01 01 <seq:u16 LE>  <net_data...>  <chunk_crc32_LE:4>  <frame_chk>
```

- `7c 00` — group/device tag (chunk container)
- `01` — session ID (mgmt/tier session)
- `01` — type = data
- `seq:u16 LE` — chunk sequence (PitHouse increments per frame)
- `net_data` — variable; structure documented below
- `chunk_crc32_LE` — standard 4-byte CRC32 over `net_data`
  (per [`../sessions/chunk-format.md`](../sessions/chunk-format.md))

## Net-data record (`ff` push)

```
ff  <size:u32 LE>  <inner_crc32_LE:4>  <kind:u32 LE>  <value:size-4 bytes LE>
```

| Offset | Size | Field | Notes |
|--------|------|-------|-------|
| 0      | 1    | tag = `0xff`        | Same `0xff` sentinel used in tier-def TLV streams; here it marks a property-push record |
| 1      | 4    | `size:u32 LE`       | Byte count of `kind ‖ value`. Equals 4 + sizeof(value). Observed: `0x08` (u32 value) and `0x0c` (u64 value) |
| 5      | 4    | `inner_crc32_LE`    | **`zlib.crc32(<kind LE><value LE>)`** stored little-endian. Verified against 7 captured samples |
| 9      | 4    | `kind:u32 LE`       | Property family / encoding selector. See table below |
| 13     | size-4 | `value:LE`        | Little-endian unsigned int. Width = `size - 4` |

After the record, the chunk's standard 4-byte CRC32 trailer covers the
entire `net_data` (including `ff`, size, inner CRC, kind, value).

## Verified samples

| Property              | size | kind | value             | inner CRC32 (wire) | Captured chunk seq |
|-----------------------|------|------|-------------------|--------------------|--------------------|
| brightness = 0        | 8    | 1    | `0x00000000`      | `f7 df 88 a9`      | (0% slider)        |
| brightness = 25       | 8    | 1    | `0x00000019`      | `e2 c7 99 84`      | 0x01cb             |
| brightness = 41       | 8    | 1    | `0x00000029`      | `43 3f b2 74`      | (41% slider)       |
| brightness = 50       | 8    | 1    | `0x00000032`      | `dd ef aa f3`      | 0x0146             |
| brightness = 100      | 8    | 14   | `0x00000064`      | `0f ad ec c4`      | repeats — baseline |
| display standby 3 min | 12   | 10   | `0x000000000002bf20` (180000 ms)  | `1a d2 61 f3` | 0x01a7 |
| display standby 25 min| 12   | 10   | `0x0000000000016e360` (1500000 ms) | `b9 d3 ce a6` | 0x01cc |

Inner-CRC verification (Python, `zlib.crc32`):

```python
>>> import zlib, struct
>>> struct.pack("<I", zlib.crc32(struct.pack("<II", 1, 25)))
b'\xe2\xc7\x99\x84'   # matches brightness=25 wire bytes
>>> struct.pack("<I", zlib.crc32(struct.pack("<IQ", 10, 1500000)))
b'\xb9\xd3\xce\xa6'   # matches standby=25min wire bytes
```

All seven samples match. The "hash" is therefore not a property identifier
nor a nonce — it is a deterministic CRC32 of `(kind ‖ value)` and serves as
a redundant integrity check over the property+value pair, on top of the
chunk-level CRC32.

## `kind` field interpretation

The `kind` field appears to encode value-type / unit / re-emit-policy
metadata. Same property at different values reuses the same `kind`:

| `kind` | Property family observed | Value width | Notes |
|--------|--------------------------|-------------|-------|
| 1      | dashboard brightness (0–100) | u32 LE | User-driven slider updates |
| 10     | display standby timeout (ms) | u64 LE | Duration in milliseconds |
| 14     | dashboard brightness = 100 baseline | u32 LE | Pushed continuously even with no UI change; `kind=1` only appears mid-slider-move. Hypothesis: `kind=14` is the "current persisted setting" re-emit; `kind=1` is "user slider in-flight". Not yet confirmed — needs capture of other settings' baselines |

`kind=14` for brightness=100 was observed every ~2 seconds with the slider
parked. After moving the slider to 25%, `kind=1` value=25 appeared **once**;
the periodic `kind=14` value=100 stream **continued** unchanged. So PitHouse
keeps re-emitting the original baseline alongside the user delta — likely a
state-sync mechanism for the wheel.

## Open questions

- Is `kind` a property-id table (so brightness is always kind 1 / 14) or a
  value-encoding selector (kind = "u32 percent" / "u64 ms")? Capture more
  properties (RPM colors, flag colors, indicator modes) to disambiguate.
- Why does brightness=100 ride `kind=14` while brightness=0/25/41/50 ride
  `kind=1`? 100 happened to be the persisted baseline at capture start;
  unclear whether 100 specifically uses `kind=14` or whether the persisted
  vs. transient distinction drives `kind`.
- Are there properties that use `size > 12` (longer values: strings, colour
  triplets, multi-field structs)? Captures so far have only u32 and u64.
- Does the wheel ACK individual property pushes? Chunks ride session 0x01
  which has the standard `fc:00` ACK path, but per-property semantics
  unconfirmed.

## Capture method

CSP wheel sim (`/home/rorth/src/moza-simhub-plugin/sim/wheel_sim.py`)
running on Linux USB gadget; PitHouse on Windows connects over USB-IP. Sim
records every received frame in `sim_recent` with handler-tag. Slider
moves on PitHouse's "MOZA Wheel" → integrated-dashboard tab produced
unique `7e 1b 43 17 7c 00 01 01 ...` (size=8) and `7e 1f 43 17 7c 00 01 01
...` (size=12) frames matching the moved property's value. Sim does not
emit these — they originate from PitHouse, confirmed by `_record(tag,
frame)` only being called on inbound frames in
[`../../sim/wheel_sim.py:3383`](../../../sim/wheel_sim.py).

## Cross-references

- [`../sessions/chunk-format.md`](../sessions/chunk-format.md) — outer
  chunk framing and chunk-level CRC32
- [`../tier-definition/version-0-url-csp.md`](../tier-definition/version-0-url-csp.md)
  — same `0xff` sentinel byte appears in the tier-def TLV stream
  (different content / different session phase)
- [`../settings/wheel-0x17.md`](../settings/wheel-0x17.md) — wheel
  config via group `0x3F` (separate path; not used by the dashboard
  brightness slider)
- [`../settings/dashboard-0x14.md`](../settings/dashboard-0x14.md) —
  standalone MDD dashboard via group `0x32` (separate physical device)
