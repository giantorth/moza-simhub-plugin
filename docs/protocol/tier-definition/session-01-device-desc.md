### Session 0x01 — device description (both directions, both models)

Wheel and Pithouse send short descriptor on session 0x01. Structure identical:

```
[0x07] [01 00 00 00] [00]                     — version 0
[0x0c] [size] [data...]                        — device-specific hash/fingerprint
[0x01] [size: u32 LE] [data...]               — descriptor body
[0x05] [00]                                    — unknown
[0x04] [size] [ch_index=0] [url or padding]   — single channel entry (index 0)
[0x06] [00]                                    — end
```

Tag 0x0c (14 bytes) differs per device — VGS: `0c 06 69 42 07 14 e8 06...`, CSP: `0c 04 8a e5 d0 86 b2 fc...`. May encode hardware ID or firmware fingerprint. Channel entry at index 0 appears to be padding (3 ASCII spaces on VGS).
