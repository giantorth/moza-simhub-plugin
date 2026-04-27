### Session 0x03 tile-server envelope (variant, 12 bytes)

**Session 0x03 uses a different 12-byte envelope format**, reversed from live PitHouse captures (2026-04-21):

```
FF 01 00 [comp_size+4 u32 LE] FF 00 [uncomp_size u24 BE]
```

| Bytes | Field | Notes |
|-------|-------|-------|
| 0 | `0xFF` marker | Constant — same sentinel used for session 0x01/0x04 FF-prefixed fields |
| 1 | `0x01` sub-msg index | Constant |
| 2 | `0x00` tag | Constant |
| 3..6 | compressed_size + 4 (u32 LE) | Observed: `FB 00 00 00` (=251, for 247-byte zlib) and `91 04 00 00` (=1169, for 1165-byte zlib). The `+4` likely accounts for the zlib stream's Adler-32 trailer |
| 7 | `0xFF` separator | Constant |
| 8 | `0x00` tag | Constant |
| 9..11 | uncompressed_size (u24 **BE**) | Big-endian unlike other sizes. Observed: `00 03 07` (=775) and `00 18 9D` (=6301) |

Only used for tile-server map metadata JSON blobs (`{"map":{"ats":"...","ets2":"..."},"root":"...","version":N}`). Plugin helper: `Telemetry/TileServerStateBuilder.BuildEnvelope()`.
