# Telemetry

Live game telemetry: channel catalog, value encoding, the `0x43/0x17 7D 23` live stream, and enable/disable control signals.

| File | Topic |
|------|-------|
| [`channels.md`](channels.md) | Channel encoding types (compression codes), ordering, namespace distribution across `Telemetry.json` (410 channels) |
| [`service-parameter-transforms.md`](service-parameter-transforms.md) | `rs21_parameter.db` `ServiceParameter` value transforms (multiply/division/custom) for setting display |
| [`live-stream.md`](live-stream.md) | Frame structure, flag-byte multi-stream architecture, F1 dashboard tier examples, capture-vs-spec verification |
| [`control-signals.md`](control-signals.md) | Dash telemetry enable (`0x41/FD DE`), sequence counter (`0x2D`), RPM LED telemetry (`0x3F/1A 00`), LED group colour (`0x3F/27`) |

Tier definitions (how host tells wheel which channels to expect): see [`../tier-definition/`](../tier-definition/).
