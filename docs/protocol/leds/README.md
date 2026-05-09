# LEDs

LED encoding and per-area control. Per-device command tables live in [`../devices/`](../devices/); this folder owns the cross-cutting prose.

| File | Topic |
|------|-------|
| [`color-commands.md`](color-commands.md) | RPM + button LED color encoding (4 bytes per LED, 5 per 20-byte chunk, `0xFF` padding rule) |
| [`base-ambient-0x20-0x22.md`](base-ambient-0x20-0x22.md) | Wheelbase ambient LED strips (groups `0x20`/`0x22`), sent to dev `0x12` |
| [`wheel-groups-0x3F-0x40.md`](wheel-groups-0x3F-0x40.md) | Wheel LED group architecture (5 groups: Shift, Button, Single, Rotary, Ambient) |

Live RPM LED telemetry frames: [`../telemetry/control-signals.md`](../telemetry/control-signals.md).
