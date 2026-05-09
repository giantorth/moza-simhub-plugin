## Dashboard settings encoding (groups `0x32` write / `0x33` read, dev `0x14`)

Per-setting value encoding for the standalone Moza MDD display. Group
`0x32` writes; group `0x33` reads. Different group / device than the
in-wheel display (`0x3F`/`0x40`/`0x17`) — the MDD is a separate physical
peripheral with its own settings store.

> **Wheel dashboards (integrated displays in formula-style wheels) are
> NOT this device.** Those use group `0x3F` on dev `0x17` for stored
> wheel config (see [`wheel-0x17.md`](wheel-0x17.md) and
> [`../devices/wheel-0x17.md`](../devices/wheel-0x17.md)). PitHouse
> additionally pushes some integrated-dashboard runtime settings
> (brightness, display-standby) via session-0x01 `ff` records; see
> [`../findings/2026-04-29-session-01-property-push.md`](../findings/2026-04-29-session-01-property-push.md).

### Frame layouts

**Write** (host → device):

```
7E [N] 32 14 [cmd] [value bytes] [checksum]
```

**Read** (host → device):

```
7E [N] 33 14 [cmd] [00 00 ...] [checksum]
```

**Read response** (device → host):

```
7E [N] B3 41 [cmd echo] [value bytes] [checksum]
```

`0xB3` = `0x33 | 0x80`; `0x41` = nibble-swap of `0x14`.

### Non-obvious value encodings

| Command | Cmd ID | Raw values | Encoding notes |
|---------|--------|------------|----------------|
| `rpm-indicator-mode` | `11 00` | 0 = Off, 1 = RPM, 2 = On | **0-based** — different from wheel (1-based) |
| `flags-indicator-mode` | `11 02` | 0 = Off, 1 = Flags, 2 = On | **0-based** |

### Wheel vs dash indexing

The same conceptual setting (`rpm-indicator-mode`) uses **different value
encodings** on the wheel vs dash:

| | Wheel (`0x3F` dev `0x17` cmd `04`) | Dash (`0x32` dev `0x14` cmd `11 00`) |
|-|------------------------------------|-------------------------------------|
| Off | 2 | 0 |
| RPM | 1 | 1 |
| On | 3 | 2 |

Never copy raw values between the two devices — translate through the
display string.

### Worked example: set dash flag mode to "Flags"

```
7E 04 32 14 11 02 01 [chk]
            │  │  │
            │  │  └── value = 1 (Flags)
            │  └───── cmd byte 2 = 0x02 (flags variant)
            └──────── cmd byte 1 = 0x11 (indicator-mode family)
```

### Other commands

The full MDD command table — RPM colors, flag colors, RPM thresholds,
brightness, blink colors, RPM display mode, RPM timings, RPM intervals —
lives in [`../devices/dash-0x14.md`](../devices/dash-0x14.md). All follow
the same `7E [N] 32 14 [cmd] [value]` write form; encoding rules above
apply only to the listed special cases.

### Cross-references

- [`wheel-0x17.md`](wheel-0x17.md) — analogous wheel settings (different
  indexing scheme)
- [`../telemetry/service-parameter-transforms.md`](../telemetry/service-parameter-transforms.md) —
  general value transforms in rs21_parameter.db
- [`../devices/dash-0x14.md`](../devices/dash-0x14.md) — full per-command
  table
