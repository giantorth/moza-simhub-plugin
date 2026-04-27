## USB topology

Device: Moza composite USB (VID `0x346E` PID `0x0006`).

| Interface | Type | Endpoints | Purpose |
|-----------|------|-----------|---------|
| MI_00 | USB serial (CDC) | 0x02 OUT / 0x82 IN | Moza protocol bus — all serial frames |
| MI_02 | HID | 0x03 OUT / 0x83 IN | Wheel axes/buttons (not telemetry) |

Device IDs (19=base, 20=dash, 23=wheel, etc.) are addresses on internal serial bus routed through wheelbase hub — not separate USB devices.

**All captured live telemetry addressed to device 0x17 (wheel).** No captures exist of telemetry sent to device 0x14 (MDD / standalone dash).
