### Known wheel model names

Confirmed from USB captures + live serial queries:

| Model name | Wheel | Source |
|------------|-------|--------|
| `VGS` | Vision GS | USB capture (`cs-to-vgs-wheel.ndjson`). 8 button LEDs, no flag LEDs |
| `CS V2.1` | CS V2 | USB capture (`vgs-to-cs-wheel.ndjson`) |

Assumed from device naming conventions (unverified):

| Prefix | Wheel | Notes |
|--------|-------|-------|
| `GS V2P` | GS V2P | 10 button LEDs (5 per side), no flag LEDs |
| `W17` | CS Pro | 18 RPM LEDs, no flag LEDs (firmware reports `W17`) |
| `W18` | KS Pro | 18 RPM LEDs, no flag LEDs (firmware reports `W18`) |
| `KS` | KS | 10 button LEDs, no flag LEDs |
| `W13` | FSR V2 | 16 RPM LEDs, 10 buttons, no flag LEDs (firmware reports `W13`) |
| `TSW` | TSW | 14 button LEDs, no flag LEDs |

### ES wheel identity caveat

ES (old-protocol) wheels share device ID `0x13` with wheelbase. Identity queries sent to `0x13` return **base** identity, not wheel. Example: ES wheel on R5 base returns `R5 Black # MOT-1` (base identity). No known way to query ES wheel's own model name through serial protocol.
