## Standalone Dash Display — MDD (Device `0x14` / 20)

> **This device is the external Moza MDD peripheral** — a separate physical unit, distinct from steering wheels with integrated display screens (device `0x17`). All captured live telemetry targets device `0x17`, not the MDD; whether the MDD uses the same protocol is unknown.

### Group `0x32` / `0x33` (50 / 51) — Settings

| Command | ID | Bytes | Type | Notes |
|---------|----|-------|------|-------|
| rpm-timings | `05` | 10 | array | |
| rpm-display-mode | `07` | 1 | int | |
| flag-colors | `08 00` | 18 | array | Write-only |
| rpm-blink-color1 | `09 00` | 3 | array | RGB; write-only |
| rpm-blink-color2 | `09 01` | 3 | array | |
| rpm-blink-color3 | `09 02` | 3 | array | |
| rpm-blink-color4 | `09 03` | 3 | array | |
| rpm-blink-color5 | `09 04` | 3 | array | |
| rpm-blink-color6 | `09 05` | 3 | array | |
| rpm-blink-color7 | `09 06` | 3 | array | |
| rpm-blink-color8 | `09 07` | 3 | array | |
| rpm-blink-color9 | `09 08` | 3 | array | |
| rpm-blink-color10 | `09 09` | 3 | array | |
| rpm-brightness | `0A 00` | 1 | int | |
| flags-brightness | `0A 02` | 1 | int | |
| rpm-color1 | `0B 00 00` | 3 | array | RGB |
| rpm-color2 | `0B 00 01` | 3 | array | |
| rpm-color3 | `0B 00 02` | 3 | array | |
| rpm-color4 | `0B 00 03` | 3 | array | |
| rpm-color5 | `0B 00 04` | 3 | array | |
| rpm-color6 | `0B 00 05` | 3 | array | |
| rpm-color7 | `0B 00 06` | 3 | array | |
| rpm-color8 | `0B 00 07` | 3 | array | |
| rpm-color9 | `0B 00 08` | 3 | array | |
| rpm-color10 | `0B 00 09` | 3 | array | |
| flag-color1 | `0B 02 00` | 3 | array | |
| flag-color2 | `0B 02 01` | 3 | array | |
| flag-color3 | `0B 02 02` | 3 | array | |
| flag-color4 | `0B 02 03` | 3 | array | |
| flag-color5 | `0B 02 04` | 3 | array | |
| flag-color6 | `0B 02 05` | 3 | array | |
| rpm-mode | `0D` | 1 | int | |
| rpm-value1 | `0E 00` | 4 | int | RPM threshold for LED 1 |
| rpm-value2 | `0E 01` | 4 | int | |
| rpm-value3 | `0E 02` | 4 | int | |
| rpm-value4 | `0E 03` | 4 | int | |
| rpm-value5 | `0E 04` | 4 | int | |
| rpm-value6 | `0E 05` | 4 | int | |
| rpm-value7 | `0E 06` | 4 | int | |
| rpm-value8 | `0E 07` | 4 | int | |
| rpm-value9 | `0E 08` | 4 | int | |
| rpm-value10 | `0E 09` | 4 | int | |
| rpm-indicator-mode | `11 00` | 1 | int | 0=Off, 1=RPM, 2=On |
| rpm-interval | `0C` | 4 | int | |
| flags-indicator-mode | `11 02` | 1 | int | 0=Off, 1=Flags, 2=On |
