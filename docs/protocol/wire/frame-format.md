## Frame format

```
7E  [N]  [group]  [device]  [payload: N bytes]  [checksum]
```

| Field | Size | Description |
|-------|------|-------------|
| Start | 1 | Always `0x7E` |
| N | 1 | Byte count of payload only (excludes group, device, checksum) |
| Group | 1 | Request group / command category |
| Device | 1 | Target device ID on internal serial bus |
| Payload | N | Command ID (1+ bytes) followed by value bytes |
| Checksum | 1 | See below |

**N (payload length)** bounded: valid range 1–64. Values outside indicate corruption or desync — discard and rescan for next `0x7E`.

**Frame sync:** receivers scan byte stream for `0x7E`, discarding non-`0x7E` bytes. Once found, next byte read as N. If N out of range or checksum fails, frame dropped and scanning resumes. Self-synchronizing after corruption or mid-stream connection.

Command IDs that are integer arrays must be provided sequentially in order. Values big-endian. Multiple frames can be concatenated in a single USB bulk transfer.

### Responses

| Field | Transform |
|-------|-----------|
| Group | Request group + `0x80` (MSB set) — e.g. `0x21` → `0xA1` |
| Device | Nibbles swapped — e.g. `0x13` → `0x31` |
| Payload length | Reflects response data size, not request |

Write requests: response mirrors request payload. Read requests: response contains full stored value regardless of request size (1-byte read probe returns full 16-byte string).

### Command chaining

Multiple commands can be sent at once. Responses **not guaranteed in request order** — match by group number.
