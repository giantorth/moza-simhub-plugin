# Findings 2026-05-15 — kind=8 catalog: unified TLV trailer + type=9 preset block (partial)

Refines the kind=8 record-layout description in
[`2026-05-07-sess02-ff-kinds-reference.md`](2026-05-07-sess02-ff-kinds-reference.md).
The earlier doc described two "sub-formats" with an unparsed boundary
structure (`TODO: fully decode sub-format B`). This finding shows both
sub-formats use the **same record envelope** — only the per-record TLV
trailer's `type` differs. Decoded against
`sim/logs/bridge-20260514-204307.jsonl` (the same PitHouse + Simple
Rally session captured for the
[sess=0x01 channel protocol finding](2026-05-14-sess01-channel-protocol-and-string-values.md)).

## Unified record envelope

Every kind=8 record is:

```
[id:u16 BE]  [name_len:u32 BE]  [UTF-16BE name (name_len bytes)]  [TLV trailer]
```

Where the **TLV trailer** is:

```
[type:u32 BE]  [reserved:u8]  [value: type-determined layout]
```

### Trailer type registry

| `type` | Trailer total | Value layout | Sample records |
|-----:|--------------:|--------------|----------------|
| 0  | 5 B  | (no value; reserved byte = `0x01` end-marker) | `RpmAbsolute1..10`, `RpmPercent1..10` |
| 2  | 9 B  | `[u32 BE]` | `buttonGroupStandbyModeV2 = 1`, `paddleStatusMode = 0` |
| 4  | 13 B | `[u64 BE]` (count / millisecond / 0–100) | `activePaddleNum = 4`, `buttonGroupBrightness = 80`, `buttonGroupStandbyBreathInterval = 3000` |
| 6  | 13 B | `[u64 BE]` interpreted as `double BE` (IEEE 754) | `equalizerGain1 = 90.0`, `naturalInertia = 400.0`, `naturalFriction = 15.0`, `naturalDamper = 42.0`, `naturalInertiaEnabled = 1.0` |
| 10 | 9 + strlen B | `[strlen:u32 BE] [UTF-16BE string]` | `__location → "en_US"` |
| 9  | variable | nested preset block — see below (partial decode) | `preset/__keyboardInfo`, … |

Decoding statistics on this capture's kind=8 payload (12,736 B
decompressed): walker reaches 82 records cleanly with the types-0/2/4/
6/10 model before hitting the first `type=9` record at offset 4448
(`preset/__keyboardInfo`).

### Worked examples (byte-exact)

`buttonGroupBrightness` (id=22, value=80):

```
00 16                            — id = 22
00 00 00 2a                      — name_len = 42 (21 chars × 2)
[42 bytes UTF-16BE "buttonGroupBrightness"]
00 00 00 04                      — trailer type = 4
00                               — reserved
00 00 00 00 00 00 00 50          — u64 BE = 80
```

`equalizerGain1` (id=112, value=90.0):

```
00 70                            — id = 112
00 00 00 1c                      — name_len = 28
[28 bytes UTF-16BE "equalizerGain1"]
00 00 00 06                      — trailer type = 6
00                               — reserved
40 56 80 00 00 00 00 00          — double BE = 90.0
```

`__location` (id=0, value="en_US"):

```
00 00                            — id = 0
00 00 00 14                      — name_len = 20
[20 bytes UTF-16BE "__location"]
00 00 00 0a                      — trailer type = 10
00                               — reserved
00 00 00 0a                      — strlen = 10
00 65 00 6e 00 5f 00 55 00 53    — UTF-16BE "en_US"
```

## Type=9 — nested preset block (partial decode)

Records with `type=9` trailers introduce a *nested* sub-block carrying
multiple internal sub-records. Observed prefix names: `preset/__*` and
the records that follow (`roadSurfacePerceptionOverallStrength`,
`roadSurfacePerceptionSensitivity`, `rotaryModeSwitch1`, …) — these
appear to be **preset config bundles** containing per-property values
that override the wheel's defaults.

### Outer envelope

For the first `type=9` record observed (`preset/__keyboardInfo`):

```
00 00 00 09                      — trailer type = 9
00 00 00 00                      — 4 zero bytes (reserved? always zero)
[20 bytes "preset metadata":
   03 00 00 00                   — u32 LE = 3 (record count? sub-protocol version?)
   00 01 00 00                   — u32 LE = 256 OR u32 BE = 0x00010000
   00 03 00 00                   — u32 LE = 0x00000300 (768) OR BE = 0x00030000
   00 00 1f 00                   — u32 LE = 0x001f0000 (31 high)
   87 00 00 00 ]                 — u32 LE = 135
[inner records start at offset 4528 — see below]
```

The 4 + 4 + 20 = 28-byte preset metadata header is observed but **not
field-mapped**. Plausible interpretations of the first u32 = 3:
record-count of inner sub-records, preset-version, or an enum value.
Need a second sample (another `type=9` record) to disambiguate.

### Inner sub-records (decoded 2026-05-15)

Each inner sub-record is **UTF-16BE** (same encoding as the outer
catalog — earlier "UTF-16LE" interpretation was an alignment artefact
of a 3-byte offset between trailer end and next-name start) with a
header + body + fixed 17-byte trailer:

```
[name_len:u16 BE]                — bytes in the UTF-16BE name
[UTF-16BE name (name_len bytes, NOT NUL-terminated)]
[trailer:17 B]:
    [reserved: 3 zero bytes]
    [type:u32 LE]                — 4 bytes
    [value:u64 BE]               — 8 bytes (interpret as u64 OR double per type)
    [reserved: 2 zero bytes]
```

Total inner-record size = 2 + name_len + 17.

The inner trailer differs from the outer in two ways: type is **LE**
(not BE), and the reserved bytes are split 3-before/2-after instead of
the outer's single reserved-byte sandwiched between type and value.

#### Verified inner records (from `preset/__keyboardInfo`)

| Offset | Name | type | u64 BE value | Interpretation |
|-------:|------|-----:|--------------:|----------------|
| 4527 | `roadSurfacePerceptionOverallStrength` | 6 | 136 | likely scaled int (percent? hundredths?) |
| 4618 | `roadSurfacePerceptionSensitivity` | 6 | 51 | similar |
| 4701 | `rotaryModeSwitch1` | 4 | 52 | mode index |
| 4754 | `rotaryModeSwitch2` | 4 | 53 | mode index |
| 4807 | `rotaryModeSwitch3` | 4 | 54 | mode index |
| 4860 | `rotaryModeSwitch4` | 4 | 180 | mode index |

Decoder lands cleanly on the first 6 records (3 records of inner
type=6 + 3 of type=4); record 7 onward uses a **different, shorter
trailer**:

- `rpmBlinkingAbsThreshold` (record 7) has a 9-byte trailer:
  `[4 zero bytes] [type-byte = 0x01] [reserved = 0x00] [value u8 = 0xB5 = 181] [2 trailing zeros]`
- `rpmBlinkingPreThreshold` (record 8) has a 10-byte trailer of the
  same shape with one extra leading zero:
  `[5 zero bytes] [type-byte = 0x01] [reserved = 0x00] [value u8 = 0x89 = 137] [2 trailing zeros]`

The inner type byte (0x01 here, vs the 32-bit 4/6 in earlier records)
suggests inner trailer length is type-dependent:

| Inner type | Trailer length | Value width | Examples |
|-----------:|---------------:|-------------|----------|
| `0x01` (u8 threshold?) | 9–10 B (varies by leading-zero count — likely alignment artifact) | `u8` | `rpmBlinkingAbsThreshold = 181`, `rpmBlinkingPreThreshold = 137` |
| `0x04` (count / millisecond / 0–100) | 17 B | `u64 BE` | `rotaryModeSwitch1 = 52`, etc. |
| `0x06` (double / signed value) | 17 B | `u64 BE` interpreted as `double` | `roadSurfacePerceptionOverallStrength = 136` |

The variable leading-zero count between records 7 and 8 is suspicious
and almost certainly indicates that one of the "leading zero" bytes is
not actually part of the trailer — it's an alignment artifact from
either a slightly different trailer length (8 vs 9 bytes core) or a
preceding field I haven't identified. Open question for the next decode
pass; needs samples where the same property carries different values
to lock the layout down.

#### Sub-record byte-exact example (`roadSurfacePerceptionOverallStrength`)

```
00 48                            — name_len u16 BE = 72 (= 36 chars × 2)
[72 bytes UTF-16BE "roadSurfacePerceptionOverallStrength"]
00 00 00                         — 3 reserved zero bytes
06 00 00 00                      — inner type u32 LE = 6
00 00 00 00 00 00 00 88          — value u64 BE = 136
00 00                            — 2 reserved zero bytes
```

Total record bytes = 2 + 72 + 17 = 91.

The preset block contains many more records (the kind=8 stream has
roughly 160 records past offset 4496 that the walker doesn't reach).
Further decoding requires either:

1. A second capture where these properties have **different known values**
   (lets us isolate the value-layout from a noise field).
2. Capturing PitHouse mid-edit of a wheel setting — the kind=8 records
   should change in lockstep with the user's actions in the Pit House UI.

## Open questions

- **Field semantics of the 20-byte preset metadata header.** Best guess is
  a (count, sub-version, salt, timestamp) tuple but no individual field
  is locked.
- **Why UTF-16LE inside type=9?** Every other UTF-16 string in kind=8
  (record names, type=10 locale values) is UTF-16BE. Type=9 inner names
  inexplicably flip to LE. Possible explanations: (a) different firmware
  team wrote the preset code, (b) the preset block is a serialised wheel-
  side data structure originally produced by little-endian firmware code
  and never re-encoded for the catalog.
- **Inner trailer's missing `reserved` byte.** Outer TLV has `[type:u32 BE]
  [reserved:u8] [value]`; inner sub-records have `[type:u32 LE] [value]`
  with no reserved byte. The byte saved is consistent with a tightly
  packed sub-protocol — but could also indicate I've miscounted on a
  record and the reserved byte is there.

## Why this matters (and why it can wait)

`kind=8` is the wheel-internal property catalog (FFB tuning, button
behaviour, equalizer presets, …) — **NOT** the game-telemetry channel
catalog. The plugin's string-channel work (see
[`2026-05-14-sess01-channel-protocol-and-string-values.md`](2026-05-14-sess01-channel-protocol-and-string-values.md))
uses the **sess=0x01 `type=0x04` catalog** for URL → idx mapping. That
path is fully decoded and wired through `ChannelCatalogParser.FindIdxByUrl`,
so the kind=8 type=9 gap doesn't block the test-mode fix.

Decoding type=9 fully is useful for:
- Future wheel-settings UI features (mapping wheel-side property IDs to
  user-facing controls).
- Round-tripping kind=8 captures back into the sim for replay.
- Confirming the wheel's preset/config snapshot matches what PitHouse
  shows the user.

## Tooling

`/tmp/parse_kind8_full.py` and `/tmp/dump_subformat_b.py` (one-shot
scripts; not promoted to `tools/` yet). Worth promoting to
`tools/bridge-kind8-decode` once the type=9 inner-record decode is
complete enough to walk the whole catalog end-to-end.
