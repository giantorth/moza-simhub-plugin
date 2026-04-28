## Wheel LED group architecture (groups `0x3F` write / `0x40` read)

Newer wheels organize LEDs into **5 independently controlled groups**
addressed via a single `[group_id]` byte in the command payload. Groups
share command IDs (`1B`, `1C`, `1D`, `1E`, `1F`, `19`, `1A`) — only the
group byte differs.

> **Source:** rs21_parameter.db plus live-capture verification on CS Pro
> (W17) for groups 0/1; groups 2-4 are documented in DB but per-frame
> support varies by wheel model.

### Group catalog

| Group ID | Name | Max LEDs | Wheels with this group | Purpose |
|----------|------|----------|------------------------|---------|
| 0 | Shift | 25 | All wheels with RPM strip | RPM indicator bar |
| 1 | Button | 16 | Most wheels | Button backlights |
| 2 | Single | 28 | KS Pro, CS Pro | Single-purpose status indicators |
| 3 | Rotary | 56 | KS Pro (5-knob), CS Pro (4-knob) | Rotary encoder ring LEDs |
| 4 | Ambient | 12 | KS Pro | Ambient / underglow lighting |

### Frame layout

```
7E [N] 3F 17 [cmd] [group_id] [...] [checksum]
```

| Byte | Value | Meaning |
|------|-------|---------|
| 0 | `0x7E` | Frame start |
| 1 | `[N]` | Payload length |
| 2 | `0x3F` | Wheel-config write group (use `0x40` for read) |
| 3 | `0x17` | Device wheel |
| 4 | cmd | See per-cmd table |
| 5 | group_id | 0..4 |
| 6+ | varies | Per-cmd value bytes |
| –1 | chk | Frame checksum |

### Per-group commands

`G` = group ID (0–4); `N` = LED index within group (0..max-1).

| Command | Cmd | Bytes | Type | Notes |
|---------|-----|-------|------|-------|
| group-brightness | `1B [G] FF` | 1 | int | Plugin command `wheel-group{G}-brightness` (G=2..4). Firmware answers regardless of hardware presence — cannot be used as a presence check |
| group-normal-mode | `1C [G]` | 1 | int | Telemetry-active mode. Plugin command `wheel-group{G}-mode` |
| group-standby-mode | `1D [G]` | 1 | int | Idle mode. Not yet exposed by plugin |
| group-standby-interval | `1E [G] [2..6]` | 2 | int | 2 = breath, 3 = circular, 4 = rainbow, 5 = drift sand, 6 = breath color. Not yet exposed |
| group-led-color | `1F [G] FF [N]` | 3 | array (RGB) | LED N static RGB. Plugin commands `wheel-rpm-color{1..25}` (G=0), `wheel-button-color{1..16}` (G=1), `wheel-group{G}-color{1..Nmax}` (G=2..4) |
| group-live-colors | `19 [G]` | 20 | array (5×idx+RGB) | Bulk live telemetry frame. **Only groups 0/1 confirmed**; 2/3/4 may or may not support |
| group-live-bitmask | `1A [G]` | 2..4 | int (LE) | Per-frame active-LED bitmask. Groups 0/1 only. Plugin `wheel-send-rpm-telemetry`, `wheel-send-buttons-telemetry` |

### Static vs live rendering pipelines

Groups 0 and 1 have **two parallel pipelines** that the firmware
multiplexes based on the active mode:

| Pipeline | Cmds | Where state lives | When it renders |
|----------|------|-------------------|-----------------|
| Static | `1F [G] FF [N]` | EEPROM (per-LED RGB) | Idle/constant mode (`telemetry-mode = 2`, `buttons-idle-effect = 1`) |
| Live | `19 [G]` + `1A [G]` | Volatile frame buffer | While telemetry is actively pumping the bitmask |

Groups 2–4 have only the static path documented — live frame writes are
not exercised by any current capture.

### Worked example: light KS Pro single-LED group LED 5 red

```
7E 06 3F 17 1F 02 FF 05 FF 00 00 [chk]
            │  │  │  │  │  │  │
            │  │  │  │  └──┴──┴ B
            │  │  │  │  └────── G
            │  │  │  │  └────── R
            │  │  │  └───────── LED index N = 5
            │  │  └──────────── (0xFF separator)
            │  └─────────────── group ID G = 2 (Single)
            └────────────────── cmd = 0x1F (group-led-color)
```

### See also

- [`color-commands.md`](color-commands.md) — frame layouts and chunk
  format for the `0x19` and `0x1A` live commands
- [`../telemetry/control-signals.md`](../telemetry/control-signals.md) —
  RPM LED telemetry (`0x1A 00`), LED group colour (`0x27 [G] [role]`)
- [`../devices/wheel-0x17.md` § Extended LED Group Architecture](../devices/wheel-0x17.md)
  — full per-group command table
- [`../wire/wheel-write-echoes.md`](../wire/wheel-write-echoes.md) —
  echo prefixes for `1F`, `19`, `1A` writes
