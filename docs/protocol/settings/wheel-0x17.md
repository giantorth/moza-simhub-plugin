## Wheel settings encoding (groups `0x3F` write / `0x40` read, dev `0x17`)

Per-setting value encoding for stored configuration on the steering wheel.
Group `0x3F` writes; group `0x40` reads. Confirmed by cross-referencing
Pithouse USB captures with the boxflat reverse-engineering project. Full
per-command table in [`../devices/wheel-0x17.md`](../devices/wheel-0x17.md).

> **Setting** here means **stored configuration** (Pit House settings UI),
> not telemetry stream. Live telemetry uses different commands on the
> same group — see [`../telemetry/`](../telemetry/).

### Frame layouts

**Write** (host → device):

```
7E [N] 3F 17 [cmd] [value bytes] [checksum]
```

**Read** (host → device):

```
7E [N] 40 17 [cmd] [00 00 ...] [checksum]
```

**Read response** (device → host):

```
7E [N] C0 71 [cmd echo] [value bytes] [checksum]
```

`0xC0` = `0x40 | 0x80`; `0x71` = nibble-swap of `0x17`.

### Non-obvious value encodings

Several settings use indexing schemes that surprise on first read:

| Command | Cmd ID | Raw values | Encoding notes |
|---------|--------|------------|----------------|
| `paddles-mode` | `03` | 1 = Buttons, 2 = Combined, 3 = Split | **1-based.** Sending `0` is **invalid** — firmware breaks all paddle input including shift paddles |
| `stick-mode` | `05` | 0 = Buttons, 256 = D-Pad | 2-byte field; D-Pad sets the **high byte** (`0x0100`), not low |
| `rpm-indicator-mode` | `04` | 1 = RPM, 2 = Off, 3 = On | **1-based** (wheel only — dash uses 0-based, see [`dashboard-0x14.md`](dashboard-0x14.md)) |

### Worked examples

**Set paddle mode to Combined:**

```
7E 03 3F 17 03 02 [chk]
            │  │
            │  └── value = 2 (Combined)
            └───── cmd = 0x03 (paddles-mode)
```

**Set stick to D-Pad (high byte = 1):**

```
7E 04 3F 17 05 00 01 [chk]
            │  └─┴── value = 0x0100 (D-Pad on high byte)
            └────── cmd = 0x05 (stick-mode)
```

**Read RPM indicator mode:**

```
Host → wheel:
7E 03 40 17 04 00 00 [chk]

Wheel → host:
7E 02 C0 71 04 [mode] [chk]
              └── 1=RPM, 2=Off, 3=On
```

### Pitfalls

- **`paddles-mode = 0` bricks paddle input.** This is a hard rule — there
  is no value `0` in the documented range, and firmware treats the
  out-of-range write as "disable paddle subsystem". Recovery requires
  rewriting with a valid 1/2/3 value or power-cycling.
- **0-based vs 1-based:** wheel and dash use **different** indexing for
  what looks like the same setting (RPM indicator mode). Never copy raw
  values between the two.

### Other commands

The full wheel-config command table — colors, brightness, RPM thresholds,
LED modes, idle effects, sleep, paddle thresholds, multi-function switches
— lives in [`../devices/wheel-0x17.md`](../devices/wheel-0x17.md). All
follow the same `7E [N] 3F 17 [cmd] [value]` write form; encoding rules
above apply only to the listed special cases.

### Cross-references

- [`dashboard-0x14.md`](dashboard-0x14.md) — analogous settings on the
  standalone MDD dash
- [`../findings/2026-04-29-session-01-property-push.md`](../findings/2026-04-29-session-01-property-push.md)
  — PitHouse pushes some wheel-integrated-dashboard runtime settings
  (brightness, display-standby) via session-0x01, separate from the
  `0x3F`/`0x40` settings path documented here
- [`../telemetry/service-parameter-transforms.md`](../telemetry/service-parameter-transforms.md) —
  general value transforms (multiply / divide / custom) in
  rs21_parameter.db
- [`../wire/wheel-write-echoes.md`](../wire/wheel-write-echoes.md) — which
  wheel writes get echoed without returning a meaningful response
