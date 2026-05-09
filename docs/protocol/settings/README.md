# Settings

Per-device setting value encoding (Pit House settings UI, not telemetry stream) and low-level EEPROM access.

| File | Topic |
|------|-------|
| [`wheel-0x17.md`](wheel-0x17.md) | Wheel settings encoding (groups `0x3F`/`0x40`, dev `0x17`) |
| [`dashboard-0x14.md`](dashboard-0x14.md) | MDD dashboard settings encoding (groups `0x32`/`0x33`, dev `0x14`) |
| [`eeprom-0x0A.md`](eeprom-0x0A.md) | EEPROM direct access (group `0x0A`, any device): table/address selection, int/float read/write, known table IDs |

Full per-device command tables: [`../devices/`](../devices/). Value-transform reference: [`../telemetry/service-parameter-transforms.md`](../telemetry/service-parameter-transforms.md).
