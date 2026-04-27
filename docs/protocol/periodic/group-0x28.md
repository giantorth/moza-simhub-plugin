### Group 0x28 (host → device 0x13, occasional)

Queries device parameters from base unit. Request format: `[sub_id] 00 00`. Response mirrors sub_id with 2 data bytes.

Observed in `connect-wheel-start-game.json` (sent twice, ~2s apart):

| Sub-cmd | Response value | Notes |
|---------|---------------|-------|
| `0x01` | `01 C2` (450) | Base parameter |
| `0x17` | `01 C2` (450) | Wheel (device 0x17) parameter — possibly FFB strength/range |
| `0x02` | `03 E8` (1000) | Base parameter |
