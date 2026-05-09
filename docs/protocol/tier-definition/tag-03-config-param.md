### Tag `0x03` — config parameter

Tag `0x03` is one of the TLV tags carried inside the session 0x01 / session
0x02 tier-definition stream (alongside `0x01` body, `0x04` channel, `0x05`,
`0x06` end, `0x07` version, `0x0c` device hash, `0xff` sentinel). It encodes
a single 32-bit configuration value whose meaning is direction- and
version-specific.

### Wire encoding

```
[0x03] [size: u32 LE] [value: u32 LE]
```

| Offset | Size | Field | Notes |
|--------|------|-------|-------|
| 0 | 1 | tag = `0x03` | |
| 1 | 4 | size (LE u32) | Always `04 00 00 00` — payload is one u32 |
| 5 | 4 | value (LE u32) | Direction- and version-dependent meaning |

### Observed values

| Direction | Tier-def version | Value | Interpretation |
|-----------|------------------|-------|---------------|
| Wheel → host | v0 | `1` | Constant on VGS and CSP |
| Host → wheel (CSP) | v0 | `1` | Mirrors wheel value — host echoes the wheel's announcement |
| Host → wheel (VGS) | v2 | `0` | Different meaning under v2 framing; semantics not yet decoded |

### Context

In v0 (URL-subscription) tier-defs, `tag 0x03` rides alongside the channel
list and acts like a protocol-version handshake — both peers agree on `1`.
In v2 (compact) tier-defs, the host's `tag 0x03 = 0` is paired with `tag
0x07 = 2` (version 2) and the per-flag tier definitions; `0` may indicate
"no schema offset" or similar but no capture has yet decoded it
authoritatively.

See [`session-01-device-desc.md`](session-01-device-desc.md) and
[`session-02-channel-catalog.md`](session-02-channel-catalog.md) for where
tag `0x03` appears in the larger TLV stream, and
[`version-0-url-csp.md`](version-0-url-csp.md) /
[`version-2-compact-vgs.md`](version-2-compact-vgs.md) for how the host
emits it under each schema.
