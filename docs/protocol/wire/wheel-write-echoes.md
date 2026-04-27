### Known wheel write echoes

Certain writes to device `0x17` (wheel) are echoed verbatim by firmware though they carry no read-back semantics (LED index, brightness, channel CC vary per call so payload-keyed replay table can't cover them). Plugin recognizes echoes via `MozaProtocol.WheelEchoPrefixes` / `IsWheelEcho()` and treats them as wheel-alive signals without logging "unmatched". Mirror of `sim/wheel_sim.py:_WHEEL_ECHO_PREFIXES`:

| Group | Device | Prefix (first bytes of payload) | Purpose |
|-------|--------|---------------------------------|---------|
| 0x3F | 0x17 | `1f 00` / `1f 01` | Per-LED color page 0/1 |
| 0x3F | 0x17 | `1e 00` / `1e 01` | Channel CC enable page 0/1 |
| 0x3F | 0x17 | `1b 00` / `1b 01` | Brightness page 0/1 |
| 0x3F | 0x17 | `1c 00` / `1d 00` / `1d 01` | Page config |
| 0x3F | 0x17 | `27 00..05` | LED group colour (group 0 = RPM, 1..5 = rotary knobs) |
| 0x3F | 0x17 | `2a 00..03` | Unknown paged commands |
| 0x3F | 0x17 | `0a 00`, `24 ff`, `20 01` | Mode / display / idle-mode |
| 0x3F | 0x17 | `1a 00` | RPM LED telemetry write |
| 0x3F | 0x17 | `19 00` / `19 01` | RPM / button LED color write |
| 0x3E | 0x17 | `0b` | Newer-wheel LED command (1-byte prefix) |
