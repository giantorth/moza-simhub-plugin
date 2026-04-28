### Session `0x02` — host response: version 2 compact tier definitions (VGS / KS Pro)

For VGS and KS Pro wheels, Pithouse sends a binary-encoded tier
definition that explicitly declares **flag byte**, **channel index**,
**compression code**, and **bit width** for each entry. The wheel's
firmware doesn't need URL metadata — it decodes the bit stream using the
host-provided schema.

> See [`../telemetry/tiers.md`](../telemetry/tiers.md) for the
> tier-concept reference: how `package_level` becomes a tier, how flag
> offsets map to tiers, and an end-to-end channel example.

> **Used by:** VGS, KS Pro (W17/W18). CSP uses v0 URL subscription (see
> [`version-0-url-csp.md`](version-0-url-csp.md)).

### Stream structure

The session 0x02 stream contains a preamble, one or more tier-definition
TLV blocks, optional tier-enable entries, and an end marker.

#### Session preamble

```
[0x07] [04 00 00 00] [02 00 00 00]                 — version tag (always 2 for v2)
[0x03] [00 00 00 00]                               — config (value=0; differs from v0's value=1)
```

| Tag | Field | Notes |
|-----|-------|-------|
| `0x07` | version | 4-byte length (`04`) + 4-byte LE u32 = `2` (selects v2 format) |
| `0x03` | config | 4-byte length (`00`) — body absent — followed by 4-byte LE u32 = `0` |

The `tag 0x03 = 0` here differs from v0's `tag 0x03 = 1`. Semantics not
fully decoded; see [`tag-03-config-param.md`](tag-03-config-param.md).

#### Tier definition TLV

```
[0x01] [size: u32 LE] [flag_byte]                  — tier definition header
  [ch_index: u32 LE] [comp: u32 LE]                — 16-byte channel entry (repeated)
  [bits: u32 LE]     [reserved: u32 LE]
[0x06] [04 00 00 00] [total_channels: u32 LE]      — end marker
```

| Field | Size | Meaning |
|-------|------|---------|
| Tier header | 5 bytes | `0x01` tag, 4-byte LE size, 1-byte flag selecting which telemetry tier this defines |
| Channel entry | 16 bytes | Repeated for each channel in the tier — index, compression code, bit width, reserved |
| `ch_index` | u32 LE | 1-based channel index assigned alphabetically |
| `comp` | u32 LE | Compression code from `Telemetry.json` (see [`../telemetry/channels.md`](../telemetry/channels.md)) |
| `bits` | u32 LE | Bit width of this channel in the live frame |
| `reserved` | u32 LE | Always `0` in observed captures |

#### Tier enable entries

```
[0x00] [01 00 00 00] [flag_offset]                 — tier enable (repeated per tier)
```

`flag_offset` is the offset (`0`, `1`, `2`, ...) selecting which tier the
enable applies to.

#### Optional second batch

Pithouse sends two batches for VGS:

1. **Probe batch** at flag `0x00..0x02` with `total_channels = 0` —
   declares the tier framework but no channels yet.
2. **Real batch** at higher flag values with the actual dashboard
   channels and the correct total count in the `0x06` end marker.

The wheel accepts telemetry on flags from either batch; channel entries
declared in the real batch override probe placeholders.

### Channel indexing

Indices are **1-based**, assigned **alphabetically by URL across all
tiers** (not per-tier). A channel that appears in tier 0 (level 30) and
tier 1 (level 500) keeps the same index in both batches.

### Compression codes

The `comp: u32 LE` field is one of the codes from
[`../telemetry/channels.md`](../telemetry/channels.md) (e.g. `0x07` =
`float`, `0x0E` = `percent_1`, `0x14` = `uint3`, `0x17` = `float_001`).
Wheel firmware uses this to decode the corresponding `bits` from the
live bit stream.

### Worked example: F1 dashboard tier 30 entry for `Gear`

Channel `Gear` is `int30`, 5 bits, alphabetic index 8:

```
01                                — tier definition tag
30 00 00 00                       — size = 48 (header + 3 channel entries × 16 bytes if there were more)
00                                — flag_byte = 0 (tier 30)
08 00 00 00                       — ch_index = 8
0D 00 00 00                       — comp = 0x0D (int30)
05 00 00 00                       — bits = 5
00 00 00 00                       — reserved
```

(Real F1 base tier has 9 channels packed into 128 bits — see
[`../telemetry/live-stream.md`](../telemetry/live-stream.md) for the
full layout.)

### Plugin builder

[`TierDefinitionBuilder.BuildTierDefinitionMessage`](../../../Telemetry/TierDefinitionBuilder.cs)
constructs the v2 stream. Flag-byte assignment is controlled by
`FlagByteMode`:

| Mode | Behavior |
|------|----------|
| 0 (zero-based, default) | First tier = `0x00`, second = `0x01`, etc. — matches Pithouse 2026-04+ |
| 1 (session-port-based) | First tier = telemetry session byte (typically `0x02`), increments — older firmware quirk |
| 2 (two-batch) | Probe + real batches as described above |

### Cross-references

- [`session-02-channel-catalog.md`](session-02-channel-catalog.md) — wheel
  side of the negotiation
- [`version-0-url-csp.md`](version-0-url-csp.md) — alternative v0 format
  for CSP wheels
- [`../telemetry/live-stream.md`](../telemetry/live-stream.md) — how the
  resulting bit stream is laid out at runtime
- [`../telemetry/channels.md`](../telemetry/channels.md) — full
  compression code → bit-width / encoding table
