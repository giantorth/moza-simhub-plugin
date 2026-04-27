## LED color commands

RPM and button LED colors use `wheel-telemetry-rpm-colors` and `wheel-telemetry-button-colors`. Fixed payload size of 20 bytes per chunk; colors split across multiple writes.

Each LED: 4 bytes `[index, R, G, B]`. Five LEDs per chunk (5 × 4 = 20). With 10 RPM LEDs = 2 chunks. With 14 button LEDs = 3 chunks (last padded to 20 bytes).

**Padding:** use index `0xFF` for unused entries, not `0x00`. Zero-padding creates `[0x00, 0x00, 0x00, 0x00]` which firmware interprets as "set LED 0 to black", causing flicker.
