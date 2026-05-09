## USB topology

Moza wheelbase enumerates as a single USB composite device exposing two
functional interfaces: a CDC ACM serial pipe carrying the Moza protocol
(every frame and session described in the rest of this folder), and a
standard HID interface providing wheel axes and button events for OS
input subsystems.

### Device descriptor

| Field | Value | Notes |
|-------|-------|-------|
| Vendor ID | `0x346E` | Moza |
| Product ID | `0x0006` | Wheelbase composite (R5 / R9 / R12, etc.) |
| Product ID | `0x1000` | AB9 active shifter — separate USB device, parallel composite (see [`../devices/ab9-shifter.md`](../devices/ab9-shifter.md)) |
| Class / SubClass / Protocol | composite (`0xEF / 0x02 / 0x01`) | Multi-interface device |

### Interface map

| Interface | Class | Endpoints | Purpose |
|-----------|-------|-----------|---------|
| MI_00 | CDC ACM (`0x02 / 0x02`) | `0x02` BULK OUT / `0x82` BULK IN | **Moza protocol bus** — every serial frame in this docs tree rides here |
| MI_01 | CDC Data | data endpoints for MI_00 | Companion data interface for the CDC ACM control interface |
| MI_02 | HID (`0x03`) | `0x03` INTERRUPT OUT / `0x83` INTERRUPT IN | Wheel axes, buttons, paddles — consumed by SimHub via standard HID, not by this protocol |

### Endpoint usage

| Endpoint | Direction | Use |
|----------|-----------|-----|
| `0x02 OUT` | host → device | All Moza-protocol writes — heartbeats, LED frames, telemetry stream, session opens, dashboard upload |
| `0x82 IN` | device → host | All Moza-protocol reads — identity replies, fc:00 ACKs, configJson state, RPC replies, debug logs |
| `0x03 OUT` | host → device | HID output reports (FFB on basic HID profile; AB9 capture shows endpoint exists but unused) |
| `0x83 IN` | device → host | HID input reports — wheel position (1 kHz), buttons, axes |

### Internal bus addressing

The wheelbase exposes one USB serial pipe but routes frames internally to
multiple peripherals. Device IDs in protocol frames (`0x12`, `0x13`,
`0x14`, `0x17`, `0x19`, `0x1A`, `0x1B`, `0x1C`, `0x1E`) are addresses on
this internal serial bus — **not separate USB devices**. The wheelbase
hub multiplexes; the host only sees one CDC pipe.

| Device ID | Hex | Role | Notes |
|-----------|-----|------|-------|
| 18 | `0x12` | Main / hub | Hub controller, USB-side endpoint of the bus |
| 19 | `0x13` | Base | Wheelbase motor controller |
| 20 | `0x14` | Dash | Standalone Moza MDD display |
| 21 | `0x15` | Wheel (secondary) | Observed in `0x43` broadcasts; purpose undecoded |
| 23 | `0x17` | Wheel (primary) | All known steering wheel models target this address |
| 25 | `0x19` | Pedals | KS Pro firmware exposes pedal sub-device at this ID |
| 26 | `0x1A` | Shifter | H-pattern / sequential — `shifter-type` setting disambiguates |
| 27 | `0x1B` | Handbrake | |
| 28 | `0x1C` | E-stop | Emergency stop button |
| 29 | `0x1D` | (reserved) | Heartbeat receiver only |
| 30 | `0x1E` | (reserved) | Heartbeat receiver only |

See [`../devices/`](../devices/) for the full per-device command catalog
and [`internal-bus.md`](internal-bus.md) for the topology tree (`monitor.json`).

### Telemetry routing

**All captured live telemetry is addressed to dev `0x17` (wheel).** No
captures exist of telemetry sent to dev `0x14` (MDD / standalone dash);
whether the MDD uses the same group `0x43` payload or a different format
is undetermined.

### Re-enumeration on disconnect

USB disconnect / reconnect (or `sim_reload` from the dev workflow) is the
fastest way to recover PitHouse from a stuck-state — Windows treats it as
a fresh device and PitHouse drops its cached "upload in progress" / "state
syncing" flags, allowing a clean re-handshake. Protocol-level session
close frames alone are sometimes insufficient.
