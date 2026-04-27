### Session 0x02 — host response: version 2 compact tier definitions (VGS)

Pithouse sends different format: flag bytes, channel indices, compression codes, bit widths. Wheel told exactly how to decode bit stream.

**Session preamble (same session as tier defs):**
```
[0x07] [04 00 00 00] [02 00 00 00]            — version 2
[0x03] [00 00 00 00]                           — config (value=0)
```

**Tier definition:**
```
[0x01] [size: u32 LE] [flag_byte]            — tier definition header
  [ch_index: u32 LE] [comp: u32 LE]         — 16-byte channel entry (repeated)
  [bits: u32 LE]     [reserved: u32 LE]
[0x06] [04 00 00 00] [total_channels: u32 LE] — end marker
```

Optionally followed by enable entries and second batch:
```
[0x00] [01 00 00 00] [flag_offset]           — tier enable (repeated per tier)
[0x01] ...                                    — second batch at higher flag values
[0x06] [04 00 00 00] [total_channels: u32 LE] — end marker with actual count
```

Pithouse sends two batches: "probe" batch at flags 0x00+ with `total_channels=0`, then "real" batch at higher flags with actual dashboard channels and total count. Wheel accepts telemetry on flags from either batch.

**Channel indices** 1-based, assigned alphabetically by URL across all tiers (not per-tier).

Compression codes: see master table in [`../telemetry/channels.md`](../telemetry/channels.md).
