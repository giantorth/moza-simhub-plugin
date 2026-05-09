# Session 0x02 FF-record kinds — protocol reference

Date: 2026-05-07. Source: bridge captures from the user's own wheel hardware
captured against a Windows PitHouse instance, located in `sim/logs/`, primarily
`bridge-20260429-163951.jsonl` (Rally V4 + Nebula, ~344 K frames, includes
init handshake + 10 dashboard switches). Tools used:
`tools/bridge-decode-ff-init`, `tools/trace-sess02-decode`.

## Summary

All meaningful host↔wheel FF-tagged property records ride on session 0x02
(NOT session 0x01 as an earlier comment in `Telemetry/TelemetrySender.cs`
suggested). The wheel will not echo dashboard-switch FF kind=4 records
nor bind post-switch tier-defs to display widgets until it has received
the host-side init handshake. The init handshake is four FF records on
sess=0x02, with the wheel responding ~3.5 s later via two FF records.

The earlier file `2026-04-29-session-01-property-push.md` describes the
inner FF-record envelope in detail; this doc complements it with the
specific kind values, their bodies, and the protocol timing.

## Wire-frame envelope

Verified against `Protocol/SessionPropertyPushBuilder.WrapFfRecord`:

```
[0xFF]                    — sentinel (1 byte)
[size:u32 LE]             — kindAndValue length (4 bytes), INCLUDES the kind prefix
[crc:u32 LE]              — zlib.crc32 of the entire kindAndValue payload (4 bytes)
[kindAndValue]            — exactly `size` bytes:
    [kind:u32 LE]         — discriminator (4 bytes)
    [payload]             — `size - 4` bytes, kind-specific
```

Total wire bytes per FF record: `9 + size`.

These records are wrapped in standard SerialStream session-data chunks
(`7c 00 02 01 <seq:u16> ...`) on session 0x02 and carry their own per-chunk
CRC32 trailer per [`../sessions/chunk-format.md`](../sessions/chunk-format.md).
Multi-chunk records (kind=8, kind=11) span multiple chunks; each chunk has
its own seq + CRC, the wheel reassembles before parsing the inner FF record.

## FF kind inventory (host ↔ wheel, sess=0x02)

| Kind | Direction | Name (working hypothesis)        | Typical wire size | Body shape                                      |
|-----:|-----------|----------------------------------|------------------:|-------------------------------------------------|
|    2 | h2b       | `init_nonce`                     | 16 B              | timestamp + magic                               |
|    4 | h2b → b2h | `DASH_SWITCH` (and its echo)     | 12 B              | slot index                                      |
|    7 | h2b       | `init_enum`                      | 12 B              | small flags / version                           |
|    8 | h2b       | `init_payload_a` (channel cat.)  | 1.7 – 2.1 KB      | length-prefixed UTF-16-BE name list, zlib       |
|    9 | both      | `periodic` (heartbeat-shaped)    | 19 – 29 B         | small struct, fires every ~1.5–2 s              |
|   10 | b2h       | `wheel_state_a`                  | 12 B              | status code (sent ~3.5 s after init handshake)  |
|   11 | h2b       | `init_payload_b` (FFB props)     | 2.5 KB            | length-prefixed UTF-16-BE name list, zlib       |
|   14 | both      | `wheel_payload`                  | 8 B – 1.7 KB      | small payload one direction, large the other   |
|   15 | h2b       | `host_setting`                   | 8 B               | u32 + checksum                                  |
|   16 | b2h       | `wheel_state_b`                  | 20 B              | status (sent right after kind=10)               |

## Init handshake timing (PitHouse first 10 s on sess=0x02)

```
t=0.050  h2b OPEN sess=02
t=0.101  b2h FC ack of OPEN seq=2
t=0.101  b2h TLV stream: tag=0x07 size=1 (proto-ver), tag=0x01 size=97
         (TIER record — wheel's own state), tag=0x04 size=9 (CHANNEL_INFO),
         tag=0x06 size=N (END marker)
t=2.105  b2h re-pushes the same TLV stream (host hasn't FC-acked yet)
t=2.917  h2b FC acks seq=6 and seq=10  (drains wheel's pending state)
t=2.970  h2b FF kind=2  (init_nonce)
t=2.970  h2b FF kind=7  (init_enum)
t=2.970  h2b FF kind=8  (init_payload_a)   ← the big channel catalog
t=3.079  h2b FF kind=11 (init_payload_b)   ← the big FFB property catalog
t=5.418  b2h FF kind=10 (wheel_state_a)    ← wheel ack of init handshake
t=5.468  b2h FF kind=16 (wheel_state_b)    ← wheel ack continuation
```

The ~2.4 s gap between the host's init burst (t=2.97) and the wheel's
first ack (t=5.42) is consistent with the wheel decompressing and
processing the kind=8 (~10 KB decompressed) and kind=11 (~10 KB
decompressed) catalogs. Without these uploads, the wheel never emits
kind=10 / kind=16, and (per the symptoms documented in
`2026-05-07-sess02-init-protocol-and-stale-catalog.md`) it does not
echo subsequent FF kind=4 dashboard-switch records.

## Body decode — kind=2 (`init_nonce`)

16-byte body, comparison across captures (decoded with
`tools/bridge-decode-ff-init <cap> --until-switch -k 2`):

```
bridge-20260428-155714.jsonl   4e3bf169 00000000 909dffff 01d7b784
bridge-20260428-155843.jsonl   a63bf169 00000000 909dffff 7d87268c
bridge-20260428-163735.jsonl   c144f169 00000000 909dffff 20a516f3
bridge-20260429-140855.jsonl   6873f269 00000000 909dffff 0f7b7cbb
```

Field layout (verified by cross-capture diff):

| Offset | Size | Field            | Value                                                |
|-------:|-----:|------------------|------------------------------------------------------|
|   0..3 |    4 | timestamp_u32_LE | Unix epoch seconds at session start; varies per cap. |
|   4..7 |    4 | reserved         | always `00 00 00 00`.                                |
|  8..11 |    4 | magic            | always `90 9d ff ff` (LE 0xFFFF9D90).                |
| 12..15 |    4 | (uncertain)      | varies per capture; possibly inner-CRC or salt.      |

**Note**: bytes 12..15 vary across captures and are NOT constant. They may be
a checksum of the first 12 bytes, or a session-specific salt the wheel
verifies. Plugin's current `Protocol/SessionPropertyPushBuilder.BuildSessionInitField2Body`
emits the current Unix time at offsets 0..3, zeros at 4..11, and bytes
`90 9d ff ff` at 12..15 — but this puts the magic in the wrong slot
(should be at 8..11) and omits any CRC/salt at 12..15. **TODO**: re-derive
bytes 12..15 from a fresh capture and update the builder; consider whether
to compute a CRC32 or copy them as captured.

## Body decode — kind=7 (`init_enum`)

12-byte body, IDENTICAL across all 4 captures:

```
03 00 00 00   00 00 00 00   83 18 92 0e
```

Field layout:

| Offset | Size | Field            | Value                                                |
|-------:|-----:|------------------|------------------------------------------------------|
|   0..3 |    4 | enum_u32_LE      | always `0x00000003`. Probably a protocol-version /   |
|        |      |                  | capability indicator.                                |
|   4..7 |    4 | reserved         | always zero.                                         |
|  8..11 |    4 | magic / checksum | always `83 18 92 0e` (LE 0x0E921883). Static across  |
|        |      |                  | captures, so this is NOT a session-derived hash.     |

`Protocol/SessionPropertyPushBuilder.BuildSessionInitField7Body(slotIndex)`
puts an arbitrary slot index at offset 4..7 and zero at 8..11 — diverging
from PitHouse's static body. **TODO**: pin this body to the static bytes
above and verify against another fresh capture before committing.

## Body decode — kind=4 (`DASH_SWITCH`)

12-byte body, well-understood (see
[`2026-04-30-dashboard-switch-3f27.md`](2026-04-30-dashboard-switch-3f27.md)).
Bridge captures show the host sends the FF kind=4 record on sess=0x02 and
the wheel **echoes back the byte-identical record** on sess=0x02 within
~77 ms. The plugin currently sends the host side correctly but **never
receives the echo from the user's wheel** — symptom that the init
handshake (kinds 2/7/8/11) hasn't engaged the wheel.

```
04 00 00 00   <slot:u32 LE>   00 00 00 00
```

## Body decode — kind=8 (`init_payload_a`, channel catalog)

Wire-format outer wrapper:

```
[kind=8:u32 LE][uncompressed_size:u32 BE][zlib_stream]
```

Decompressed payload is a list of records using TWO sub-formats:

### Sub-format A — RpmAbsolute / RpmPercent slot definitions (first 20 records)

Each record:
```
[id:u16 BE]            sequential 2, 3, 4, ...
[name_len:u32 BE]      length in bytes of the UTF-16-BE name (no NUL)
[name:UTF-16-BE]       channel name
00 00 00 00 01         5-byte separator (constant)
```

Example records: `RpmAbsolute1`, `RpmAbsolute10`, `RpmAbsolute2`, …,
`RpmPercent9`. Names sort alphabetically (hence the `1`, `10`, `2`
ordering). These appear to be **dashboard data slots** the wheel uses
to bind RPM-related widgets.

### Sub-format B — wheel internal property names (record 21 onwards)

Sub-format B records have a different shape with embedded sub-fields and
localized strings (`__location` → `en_US`, etc.). Names observed:
`activePaddleNum`, `aidedSpringControl`, `baseMotorType`,
`bondingPiont` (sic), `buttonGroupBrightness`,
`buttonGroupStandbyBreathInterval`, …, `temperatureControlStrategy`,
`steeringWheelInertiaRatio`. These are wheel-config / settings property
names, not telemetry channels.

Sub-format B's record boundary structure has not been fully decoded yet;
strict parsing halts at offset 684 with separator `00 00 00 0a 00`
instead of `00 00 00 00 01` — see `tools/bridge-decode-ff-init` for the
heuristic walker that gets through it. **TODO**: fully decode sub-format B.

### Important: NOT a copy of `Data/Telemetry.json`

Across all 235 names in kind=8 (mix of sub-formats A and B), there is
**zero overlap** with the channel names in `Data/Telemetry.json`
(454 entries: `Rpm`, `MaxRpm`, `Gear`, `ABS`, `SpeedKmh`, …).
`Data/Telemetry.json` is the *game telemetry channel* catalog that the
host is subscribing TO; kind=8 is the *wheel-internal property* catalog
the host is uploading INTO the wheel.

### Size grows over a session

In `bridge-20260429-163951.jsonl` PitHouse emits kind=8 multiple times
with sizes `1740, 1769, 1780, 1807, 1832, 1865, …, 2050` bytes — each
larger than the last. This suggests PitHouse adds new entries
incrementally as the user touches different settings or widgets, and the
wheel reconciles each upload with its internal master table.

## Body decode — kind=11 (`init_payload_b`, FFB property catalog)

Wire-format outer wrapper: same as kind=8 (4-byte uncompressed-size BE
prefix + zlib stream).

Decompressed payload is a list of records with a SIMPLER format than
kind=8:

```
[id:u32 BE]            sequential 0, 1, 2, ...
[name_len:u32 BE]      length in bytes of the UTF-16-BE name (no NUL)
[name:UTF-16-BE]       FFB-property name
(no separator)
```

Example names (all observed are FFB tuning parameters):
`decrementEqualizerGain1` ... `decrementEqualizerGain6`,
`decrementGameForceFeedbackFilter`,
`decrementGameForceFeedbackStrength`,
`decrementInitialSpeedDependentDamping`,
`decrementMaximumGameSteeringAngle`,
`decrementMaximumSteeringAngle`,
`decrementMaximumTorque`,
`decrementMechanicalDamper`,
`decrementMechanicalFriction`,
`decrementMechanicalSpringStrength`,
`decrementNaturalDamper`,
`decrementNaturalFriction`,
`decrementNaturalInertia`,
`decrementSoftLimitStiffness`,
`decrementSpeed*`, …

This is presumably the `decrement*` half; the equivalent `increment*` and
setter records likely follow.

## Body decode — kinds 9, 10, 14, 15, 16 (heartbeats / state)

Brief observations; each warrants its own note when used:

- **kind=9** — fires every ~1.5–2 s in BOTH directions. Body 19–29 B,
  varies per record. Likely a ticker / sync ping.
- **kind=10** — wheel-only, 12 B. Sent ~3.5 s after the host completes
  init kinds 2/7/8/11. Almost certainly the "init complete" ack.
- **kind=14** — both directions, varying size (8 B for small messages,
  ~1.7 KB zlib-compressed for big ones). Looks like a per-event payload
  channel; large variant likely carries dashboard / config blobs.
- **kind=15** — host-only, 8 B. Looks like a small u32 setting write
  (e.g. brightness slider).
- **kind=16** — wheel-only, 20 B. Sent immediately after kind=10. Pairs
  with kind=10 to fully ack init.

## Reusable tools

- `tools/bridge-decode-ff-init <capture>` — full inventory + decode of
  all FF kinds in a bridge capture, with cross-chunk reassembly and
  zlib-decompression for the big ones.
- `tools/trace-sess02-decode <wire-trace>` — same shape against our
  own wire-trace JSONL. Reports the gap: `h2b sess=0x02: no chunks` is
  the smoking gun that the plugin never engaged the protocol.
- `tools/tierdef-decode <trace>` — already extended to flag any
  TIER channel record with `chIndex=0` (catalog lookup miss).

## Open questions

1. **Are kind=8 / kind=11 byte content firmware-version-dependent?** The
   property names look firmware-specific. Replaying captured bytes from
   one firmware against a different firmware may fail validation. We
   need a fresh PitHouse-on-current-firmware capture to test.

2. **Which subset of the four init kinds does the wheel actually
   require?** The wheel's kind=10/16 ack arrives 2.4 s after the full
   handshake — we don't yet know if any kind alone is sufficient.

3. **What are the bytes 12..15 of kind=2 (`init_nonce`)?** Vary per
   capture, semantics unclear.

4. **What is the structure of kind=8 sub-format B?** Strict parser halts;
   need a careful walk to fully decode the wheel-config records.

5. **Does the wheel echo kind=4 if we send the captured kind=2/7/8/11
   bytes verbatim?** Concrete experiment that would close the loop on
   whether content-correctness is the gate.
