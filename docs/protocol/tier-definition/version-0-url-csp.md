### Session 0x02 — host response: version 0 URL subscription (CSP)

For CSP, Pithouse responds on session 0x02 with same tag 0x04 format — echoing back channel URLs as subscription confirmation. Wheel firmware knows compression types internally.

```
[0xff]                                         — sentinel / reset
[0x03] [04 00 00 00] [01 00 00 00]            — config (value=1)
[0x04] [size: u32 LE] [ch_index: u8] [url: ASCII]  — per-channel subscription (repeated)
[0x06] [04 00 00 00] [total_channels: u32 LE] — end marker
```

Pithouse sends twice in rapid succession (first immediately after session open, then again after acks arrive). Confirmed from `CSP captures/pithouse-complete.txt` (20 channels, identical to wheel catalog).
