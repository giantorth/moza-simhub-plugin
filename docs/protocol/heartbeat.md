## Heartbeats, keepalives, and unsolicited messages

Three concurrent presence/keepalive streams run alongside the live telemetry stream.
None carries payload data — each exists to keep firmware state machines from
timing out the host or to surface device-side debug information.

| Stream | Group | Device(s) | Cadence | Payload | Purpose |
|--------|-------|-----------|---------|---------|---------|
| Bus heartbeat | `0x00` | every dev `0x12..0x1E` (13 IDs) | ~1 Hz | none (`N=0`) | Per-device presence ping |
| Dash keepalive | `0x43` | `0x14`, `0x15`, `0x17` | ~1 Hz | `00` (`N=1`) | Connection-level ping; device replies `0x80` |
| Sequence counter | `0x2D` | `0x13` | ~30–50 Hz | `F5 31 00 00 00 [seq]` | Frame-sync counter (see [`telemetry/control-signals.md`](telemetry/control-signals.md)) |

### Group `0x00` bus heartbeat

```
7E 00 00 [device] [checksum]
```

| Byte | Value | Meaning |
|------|-------|---------|
| 0 | `0x7E` | Frame start |
| 1 | `0x00` | Payload length (no payload) |
| 2 | `0x00` | Group |
| 3 | `[dev]` | Target device ID (one frame per device) |
| 4 | `[chk]` | Frame checksum |

Plugin builds 13 cached frames at start-up, one per device ID 18..30, and
sends the subset matching `DetectedDeviceMask` once per slow tick
(`TelemetrySender.SendHeartbeat`, `_cachedHeartbeatFrames` in
[`Telemetry/TelemetrySender.cs:1908`](../../Telemetry/TelemetrySender.cs)).
Sub-set sending — only emitting heartbeats for IDs that responded to the probe
phase — keeps the bus quiet for absent devices.

### Group `0x43` dash keepalive (1-byte ping)

```
7E 01 43 [device] 00 [checksum]
```

| Byte | Value | Meaning |
|------|-------|---------|
| 0 | `0x7E` | Frame start |
| 1 | `0x01` | Payload length |
| 2 | `0x43` | TelemetrySendGroup |
| 3 | `0x14`, `0x15`, `0x17` | Dash, secondary wheel address, primary wheel |
| 4 | `0x00` | Payload (always zero) |
| 5 | `[chk]` | Frame checksum |

Wheel responds with `0x80` (group toggled). Plugin builds one frame per target
device at start-up (`BuildKeepaliveFrame` in `TelemetrySender.cs:1965`) and
sends all three each slow tick. Distinct from group `0x00` heartbeats and
SerialStream `fc:00` ACKs — neither replaces the other.

### Group `0x43` length-2 broadcast (~5 s cadence)

A second `0x43` form (payload length 2, payload not yet decoded) is sent to
dev `0x14` and `0x15` every ~5 s in PitHouse captures. Plugin does not
implement; semantics undecoded.

### Unsolicited device traffic

Wheel and base emit several streams without host prompting:

| Group | Source dev | Cadence | Payload | Notes |
|-------|------------|---------|---------|-------|
| `0x0E` | wheel (`0x17`) | ~0.5 Hz | ASCII debug log | NRF radio stats, e.g. `NRFloss[avg:0.00000%] recvGap[avg:4.70100ms]` |
| `0x0E` | base (`0x13`) | ~0.5 Hz | ASCII debug log | EEPROM write traces, e.g. `INFO]param_manage.c:340 Table 2, Param 43 Written: 0` |
| `0x06` | wheel (`0x17`) | host-prompted in newer captures | 12-byte hardware identifier | VGS reply: `be 49 30 02 14 71 35 04 30 30 33 37`. See [`identity/wheel-probe-sequence.md`](identity/wheel-probe-sequence.md) |

Group `0x0E` is also used host → device as the parameter-table reader
(see [`periodic/group-0x0E-param-reader.md`](periodic/group-0x0E-param-reader.md));
device-initiated `0x0E` frames carry firmware log output rather than
parameter responses.
