### Session `0x01` — device description (both directions, both models)

Both the wheel (during connect) and Pit House (during host config phase)
exchange a short **device description** TLV stream on session `0x01`.
The structure is identical in both directions and across all wheel
models — only the contents of the device-fingerprint tag differ per
device.

### TLV stream layout

The full device-description body, in TLV order:

```
[0x07] [01 00 00 00] [00]                          — version tag (always 0)
[0x0c] [size] [data...]                            — device-specific hash / fingerprint
[0x01] [size: u32 LE] [data...]                    — descriptor body
[0x05] [00]                                        — unknown 1-byte flag
[0x04] [size] [ch_index=0] [url or padding]        — single channel entry (index 0)
[0x06] [00]                                        — end marker
```

| Tag | Field | Notes |
|-----|-------|-------|
| `0x07` | version | 4-byte length + 1-byte version. Value `0` in every observed device-description (both VGS and CSP) |
| `0x0c` | device fingerprint | 14-byte block. **Differs per device** — likely encodes hardware ID or firmware fingerprint |
| `0x01` | descriptor body | 4-byte LE length + arbitrary body bytes. Free-form; semantics not yet decoded |
| `0x05` | unknown flag | 1-byte tag, 1-byte value (always observed `0`) |
| `0x04` | channel entry | 4-byte LE length + 1-byte channel index + URL/padding. Channel index `0` is reserved for padding (3 ASCII spaces on VGS) |
| `0x06` | end marker | 1-byte tag, 1-byte value (always `0`) |

### Observed `0x0c` device fingerprints

| Wheel | First 14 bytes |
|-------|----------------|
| VGS | `0c 06 69 42 07 14 e8 06 …` |
| CSP | `0c 04 8a e5 d0 86 b2 fc …` |

The leading byte after the tag (`0x06` for VGS, `0x04` for CSP) is the
length of the value — but the value itself is opaque. No hash function
yet matches it to any device-side string (mcUid, serial, MAC, firmware
checksum). Treated as identity blob; replayed verbatim by sim per
captured wheel profile.

### Channel index 0

The `0x04` channel entry at index 0 carries placeholder content rather
than a real channel URL — VGS sends 3 ASCII spaces (`20 20 20`), CSP
varies. Channel index 0 is reserved system-wide; real channels start at
index 1. Subsequent `0x04` tags (sent on session 0x02, see
[`session-02-channel-catalog.md`](session-02-channel-catalog.md)) carry
real URLs at indices 1+.

### Direction symmetry

Wheel and host send **byte-for-byte** the same TLV structure on session
0x01. The wheel populates its `0x0c` block with its own fingerprint; the
host populates with PitHouse's fingerprint. The descriptor exchange is a
mutual identification step before tier-definition negotiation begins.

### Cross-references

- [`handshake.md`](handshake.md) — full bidirectional sequence including
  this session-0x01 stream
- [`tag-03-config-param.md`](tag-03-config-param.md) — separate config-
  param tag (rides on session 0x02, not 0x01)
- [`session-02-channel-catalog.md`](session-02-channel-catalog.md) —
  follow-on TLV stream where real channels are advertised
