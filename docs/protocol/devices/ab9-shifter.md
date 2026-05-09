## AB9 active shifter (2026-04-24)

Captures: `usb-capture/AB9/{Shifter mode change,PitHouse settings change,Launch and H-pattern gear engage,SQ gear change}.pcapng` plus event-time spreadsheet `Moza AB9.xlsx`. Captured on Windows host with PitHouse driving the shifter while wheelbase was also attached.

### USB enumeration

AB9 enumerates as its **own** Moza composite USB device (VID `0x346E` PID `0x1000`), parallel to the wheelbase (PID `0x0006`). Same 3-interface layout: CDC ACM (EP 0x02 OUT / 0x82 IN, the Moza protocol bus) + HID (EP 0x03 OUT / 0x83 IN). Host writes to the AB9 use the AB9's own CDC pipe — they do **not** route through the wheelbase. HID-OUT (0x03) was never used in any capture; all configuration travels on CDC.

Address-disambiguation (only Moza devs in capture): wheelbase OUTs target dev IDs 0x13/0x14/0x15/0x17/0x19/0x1A/0x1B/0x1E (full sub-bus). AB9 OUTs target only `Main/Hub (0x12)` — confirms AB9 has its own internal "Main" with no sub-devices.

### Shifter mode set — `Group 0x1F → dev 0x12, cmd 0xD300`

Six mode-change events at 5/10/15/20/25/30 s in the shifter-mode capture, each one 8-byte CDC OUT frame on the AB9:

| PitHouse mode | `Config Data` byte |
|---------------|--------------------|
| 5+R Layout 1 | `0x00` |
| 6+R Layout 1 | `0x04` |
| 6+R Layout 2 | `0x05` |
| 7+R Layout 1 | `0x06` |
| 7+R Layout 2 | `0x07` |
| Sequential   | `0x09` |

Gaps `0x01..0x03`, `0x08` are presumably 5+R Layout 2 / other layouts not exercised. Frame shape: `7E 03 1F 12 D3 00 <val> <chk>`.

### Stored-on-device settings — `Group 0x1F → dev 0x12`

PitHouse-settings capture wrote 6 of the 10 sliders as 8-byte `Group 0x1F` frames with single-byte payload (decimal value):

| PitHouse slider                 | Cmd ID  |
|---------------------------------|---------|
| Gear Shift Mechanical Resistance | `0xD600` |
| Spring                           | `0xAF00` |
| Natural Damping                  | `0xB000` |
| Natural Friction                 | `0xB200` |
| Maximum Output Torque Limit      | `0xA900` |

Plus one larger write for **Gear Shift Vibration** (slider event at t=25s/30s in capture): 24-byte CDC frame, `Group 0x20 cmd 0x0A01`, 17-byte payload (the dissector mislabels group 0x20 as "Base Ambient LED Write"). Payload encodes the trigger pattern; high-value byte differs between intensity 100 (`33 2c …`) and intensity 0 (`00 00 …`) at the same payload offset.

### Sliders that produced **no** USB write

Four PitHouse-settings events generated **zero** host→device packets on either USB device:

- **Gear Damping** (events at 15s, 20s)
- **Gear Notchiness** (35s, 40s)
- **Engine vibration intensity** (45s, 50s)
- **Engine vibration frequency** (55s, 60s)

Verified across both EP 0x02 OUT (CDC) and EP 0x03 OUT (HID, never used) on the AB9, and across all OUT pipes on the wheelbase. The slider moves were real (different from/to values per spreadsheet), so PitHouse either: (a) batches these to an "Apply" / save action that wasn't pressed during this capture, (b) renders engine vibration host-side and streams it as continuous output (not configuration), or (c) caches them locally until the next session/connect. Not yet disambiguated — needs a capture with the Apply button pressed, or a SimHub-driven RPM telemetry stream while engine-vib intensity is non-zero.

### Shift-trigger feedback is firmware-driven; engine vibration needs telemetry

The H-pattern (1st→7th + reverse, 18 engage/neutral transitions over 30 s) and SQ gear (6 up/down events incl. holds over 14 s) captures contain **no host→device FFB writes** during shifts. Only sparse identity probes and `Output(d4/d7/d8)` polls — same heartbeat traffic seen at idle. HID IN on EP 0x83 streams continuously at ~1 kHz regardless of shift activity.

For *shift-triggered* effects (notchiness, gear-shift vibration, damping during engage): **firmware-driven**. Host pushes static configuration once, then AB9 firmware detects shifts mechanically, plays back stored vibration pattern + notch/damping per stored settings, and reports new gear via HID IN. Host plays no real-time role in shift feel.

For *engine vibration* (intensity + frequency sliders) and any other RPM- or speed-modulated effect: **must consume game telemetry**, but path is **not visible in these captures because no sim was running**. Three plausible paths, none yet observed:

1. `Group 0x43 (Telemetry / SerialStream)` pushed over AB9's own CDC pipe (PID `0x1000` EP `0x02` OUT) — mirrors how host pushes `0x43` to dev `0x14/0x15/0x17` on the wheelbase pipe (282 such frames in the PitHouse-idle capture, all targeting wheelbase sub-devices, none targeting AB9).
2. HID Output reports on EP `0x03` OUT (interface 2). Endpoint exists but was unused across all four captures. Report descriptor was not re-fetched in capture (Windows cached), so its FFB output structure can't be inferred from this dataset.
3. Wheelbase relays telemetry over its inter-device link to the AB9 — only plausible if the wheelbase has a side channel (CAN/CSP) we can't see on USB. Wheelbase OUT to dev `0x1A (Shifter)` in these captures contained only Heartbeat, no telemetry payload.

Need a capture with a sim title running while engine vibration is enabled to disambiguate. Easiest test: open a game, log all OUT on PID `0x1000` and on the wheelbase pipe addressed to dev `0x1A`, and grep for `Group 0x43` or for any continuous HID OUT to EP `0x03` of the AB9.

Same caveat applies to **why the four sliders (Gear Damping, Notchiness, Engine vib intensity, Engine vib frequency) had no setting write in the PitHouse-idle capture**: if they are host-applied modulators rather than firmware-stored parameters, they wouldn't generate a settings write on slider movement — they'd take effect only when the host starts feeding telemetry. PitHouse may also defer them to an explicit Apply/Save action that wasn't taken during this capture.
