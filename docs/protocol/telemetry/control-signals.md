## Telemetry control signals

### Dash telemetry enable (group 0x41, device 0x17, cmd `[0xFD, 0xDE]`)

Sent ~100×/s. Data always `00 00 00 00`. Likely mode/enable flag — value 0 = telemetry active.

### Sequence counter (group 0x2D, device 0x13, ~50 Hz)

Cmd `[0xF5, 0x31]`. Data: `00 00 00 XX` where XX increments by 1 each send. Base unit sequence counter.

### RPM LED telemetry (group 0x3F, device 0x17, cmd `[0x1A, 0x00]`)

Sent ~once/s. 8 data bytes = 4 × 16-bit LE values:

```
[current_pos, 0x0000, 0x03FF, 0x0000]
```

- `current_pos = current_rpm / max_rpm × 1023` — 10-bit RPM fraction
- Value 3 always 1023 (fixed denominator)
- Values 2 and 4 always 0

### LED group colour (group 0x3F, device 0x17, cmd `[0x27, <group>, <role>]`)

Sets the **idle** and **active** colours for an entire LED group on new-protocol
wheels. Wire frame (6-byte body + checksum):

```
7E 06 3F 17 27 <group> <role> <R> <G> <B> <chk>
```

- `group` — LED group selector:
  - `0x00` — RPM strip (central LED bar)
  - `0x01..0x05` — rotary knobs 1..5 (CS Pro has 4 knobs, KS Pro has 5). Group
    indices beyond the physical knob count are silently ignored by firmware.
- `role` — colour role:
  - `0x00` — background / idle (colour shown while the knob is stationary or
    the RPM bar is unlit)
  - `0x01` — primary / active (colour flashed on rotation, or used as the lit
    RPM colour when telemetry isn't driving the bar)
- `R G B` — 24-bit RGB, 0x00..0xFF each channel.

Captured examples (CS Pro, W17):

```
7E 06 3F 17 27 01 00 FF 00 00 0E   # knob 1 background = red
7E 06 3F 17 27 01 01 FF FF FF 0D   # knob 1 primary   = white
7E 06 3F 17 27 03 00 00 FF 00 10   # knob 3 background = green
7E 06 3F 17 27 03 01 FF 00 00 11   # knob 3 primary   = red
```

Wheel echoes `(group | 0x80)` / swapped device nibble / payload mirror — plugin
recognizes via `WheelEchoPrefixes` entries for `(0x3F, 0x17, 0x27, 0x00..0x05)`.
Not readable — the `0x27 <group> 0xFF` form reads *brightness* for the same
group, not colour. Plugin persists the last-written values in
`MozaPluginSettings.WheelKnobBackgroundColors` / `WheelKnobPrimaryColors` (and
the matching fields on `MozaWheelExtensionSettings` / `MozaProfile`) and
re-pushes them on wheel detect.

Command names in `MozaCommandDatabase`: `wheel-knob{1..5}-bg-color`,
`wheel-knob{1..5}-primary-color` (3-byte array payload = RGB).
