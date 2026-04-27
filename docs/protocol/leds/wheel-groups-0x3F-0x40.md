## Wheel LED group architecture (groups 0x3F/0x40 — 63/64, extended)

`rs21_parameter.db` reveals newer wheels organize LEDs into **5 independently controlled groups**. Full per-group command table: [`../devices/wheel-0x17.md` § Extended LED Group Architecture](../devices/wheel-0x17.md#extended-led-group-architecture-groups-0x3f--0x40).

| Group ID | Name | Max LEDs | Purpose |
|----------|------|----------|---------|
| 0 | Shift | 25 | RPM indicator bar |
| 1 | Button | 16 | Button backlights |
| 2 | Single | 28 | Single-purpose status indicators |
| 3 | Rotary | 56 | Rotary encoder ring LEDs |
| 4 | Ambient | 12 | Ambient / underglow lighting |
