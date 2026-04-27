### Session 0x02 — channel catalog (wheel → host, both models)

Wheel sends supported channels. Identical structure VGS and CSP:

```
[0xff]                                         — sentinel / reset marker
[0x03] [04 00 00 00] [01 00 00 00]            — config param (value=1, constant)
[0x04] [size: u32 LE] [ch_index: u8] [url: ASCII]  — per-channel (repeated)
[0x06] [04 00 00 00] [total_channels: u32 LE] — end marker
```

VGS reports 16 channels (BestLapTime, Brake, CurrentLapTime, DrsState, ErsState, FuelRemainder, GAP, Gear, LastLapTime, Rpm, SpeedKmh, Throttle, TyreWear×4). CSP reports 20 channels (adds ABSActive, ABSLevel, TCActive, TCLevel, TyrePressure×4, TyreTemp×4).

Catalog tells host what currently loaded dashboard subscribes to. Channel indices 1-based, sorted alphabetically by URL.
