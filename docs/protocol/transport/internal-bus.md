## Internal bus topology (`monitor.json`)

`monitor.json` ships in the Pit House installation (`bin/monitor.json`) and
defines the **device tree** for each base model — which peripherals can be
attached, which port each connects to, and how the hub multiplexes them
onto the single USB serial pipe described in [`usb-topology.md`](usb-topology.md).

### Bus IDs vs serial protocol device IDs

The IDs in `monitor.json` are **bus port numbers** (which physical socket
on the wheelbase a device plugged into), not the protocol-level device
addresses used in frames. The hub maps bus IDs onto protocol IDs:

| Bus ID | Protocol device ID | Role |
|--------|--------------------|------|
| 1 | n/a | USB host (PC side) |
| 2 | `0x12` | Main / hub |
| 3 | `0x13` | Base / motor controller |
| 4 | `0x17` | Wheel (primary) |
| 5 | `0x14` | Dashboard (standalone MDD) |
| 6..12 | varies | Peripheral ports (pedals, shifters, handbrake) |
| 13, 14 | child of bus 9 | Sub-devices on a multi-port child |
| 16 | child of bus 7 | Sub-device |
| 17 | `0x14`-attached | Dash sub-device (display sub-module) |
| 18 | `0x17`-attached | Wheel display unit |

A frame addressed to dev `0x17` is routed by the hub to whatever device
is on the corresponding bus port; the hub-side mapping is defined by the
base model.

### Common topology (single-controller bases)

```
1 (USB host)
└── 2 (Main controller / hub — dev 0x12)
    ├── 3 (Motor controller — dev 0x13)
    ├── 4 (Wheel — dev 0x17)
    │   └── 18 (Wheel display unit)
    ├── 5 (Dashboard — dev 0x14)
    │   └── 17 (Dash sub-device)
    ├── 6..12 (Peripheral ports)
    ├── 13, 14 (children of 9)
    └── 16 (child of 7)
```

### Per-base variations

| Base model | Variation |
|------------|-----------|
| R5, R9, R12 (Black) | Standard topology above |
| D11 (R21 / R25 / R27 Ultra) | **Omits bus 5** — these high-end bases lack the integrated dash port; standalone MDD must connect via a peripheral port instead |
| S09 CM2 dash | Connects as **bus 19 directly off bus 2** — bypasses the standard bus-5 dashboard slot |

### Why this matters for protocol implementation

- **Heartbeat targeting:** plugin sends group `0x00` heartbeats only to
  device IDs in `DetectedDeviceMask`. Without `monitor.json`-style
  knowledge of which peripherals are physically present, plugin must
  auto-detect via probe responses and skip unanswered IDs.
- **Telemetry routing:** group `0x43` `0x7D 0x23` lands on `0x17` (wheel).
  If the bus has no integrated wheel display (e.g. CS V2.1), telemetry
  is still accepted but rendered to LEDs only — the dashboard pages
  defined in the active mzdash are ignored.
- **Sub-device wrapping:** the wheel display unit (bus 18) and dash
  sub-device (bus 17) are accessed via wrapped `0x43` frames addressed
  to their parent — see [`../identity/display-sub-device.md`](../identity/display-sub-device.md).

### Plugin auto-detection

Plugin doesn't read `monitor.json` directly. Instead, it sends an identity
probe cascade to **every** dev ID 18..30 during connect; devices that
answer get bits set in `DetectedDeviceMask`, and subsequent traffic is
gated on detection (e.g. flag-LED writes wait for dash detection because
flag LEDs live on the dash sub-device).

See [`MozaPlugin.ProbeMozaDevice`](../../../MozaPlugin.cs) for the probe
flow and [`Devices/MozaDeviceConstants.cs`](../../../Devices/MozaDeviceConstants.cs)
for the device-ID enumeration.
