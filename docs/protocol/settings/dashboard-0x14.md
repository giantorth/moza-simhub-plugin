### Dashboard settings (group 0x32/0x33, device 0x14)

| Command | ID | Raw values | Notes |
|---------|-----|-----------|-------|
| rpm-indicator-mode | `11 00` | 0=Off, 1=RPM, 2=On | **0-based** — different from wheel |
| flags-indicator-mode | `11 02` | 0=Off, 1=Flags, 2=On | **0-based** |

Wheel and dashboard use different base indices (wheel 1-based, dashboard 0-based).

See [serial.md](serial.md) and [serial.yml](serial.yml) for full command tables.
