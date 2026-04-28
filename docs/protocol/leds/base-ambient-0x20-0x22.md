## Base ambient LED control (groups `0x20` write / `0x22` read)

Two LED strips of 9 LEDs each on the wheelbase body, controlled via
write group `0x20` (32) and read group `0x22` (34). Sent to the main
controller at dev `0x12`. Documented in `rs21_parameter.db` but not
observed in any USB capture — Pit House appears not to drive these LEDs
during normal operation, so per-byte verification is pending.

> **Source:** rs21_parameter.db only. Frame examples below are derived from
> the DB encoding rules; values returned by real hardware on the read
> companion (`0x22`) have not been captured.

### Frame layout

```
7E [N] 20 12 [cmd] [value bytes] [checksum]
```

| Group | Direction | Cmd ID range | Notes |
|-------|-----------|--------------|-------|
| `0x20` (32) | host → device | per-cmd (see table) | Write — sets ambient LED state |
| `0x22` (34) | host → device | per-cmd | Read — returns currently stored value |

Read responses use `0xA0` / `0xA2` (group | 0x80) and `0x21` (nibble-swap
of `0x12`).

### Per-command summary

Full table in
[`../devices/main-hub-0x12.md` § Group `0x20` / `0x22`](../devices/main-hub-0x12.md).
Selected commands:

| Command | Cmd ID | Bytes | Type | Value semantics |
|---------|--------|-------|------|-----------------|
| `indicator-state` | `1C` | 1 | int | On (1) / off (0) |
| `standby-mode` | `1D` | 1 | int | 0 = constant, 2 = breath, 3 = cycle, 4 = rainbow, 5 = flow |
| `standby-interval` | `1E [mode]` | 2 | int | Interval value depends on mode parameter |
| `brightness` | `1F 02` | 1 | int | 0..255 |
| `led-color` | `20 [strip] [mode] [led]` | 3 | array (RGB) | strip=0/1, mode=1 (constant) / 2 (breath), led=0..8 |
| `sleep-mode` | `21` | 1 | int | |
| `sleep-timeout` | `22` | 2 | int | Seconds before sleep |
| `sleep-led-color` | `25 [strip] 01 [led]` | 3 | array (RGB) | Per-LED breathing color in sleep |
| `startup-color` | `26` | 3 | array (RGB) | Color shown briefly at power-on |
| `shutdown-color` | `27` | 3 | array (RGB) | Color shown at power-off |

### Worked example: set strip 0 LED 4 to magenta in constant mode

```
7E 06 20 12 20 00 01 04 FF 00 FF [chk]
                │  │  │  │  │  │  │
                │  │  │  │  │  │  └ B
                │  │  │  │  │  └─── G
                │  │  │  │  └────── R
                │  │  │  └───────── led index = 4
                │  │  └──────────── mode = 1 (constant)
                │  └─────────────── strip = 0
                └────────────────── cmd = 0x20 (led-color)
```

### Why two groups for one feature

The dual-group `0x20` / `0x22` split (write vs read) follows the same
convention as group `0x28`/`0x29` for the wheelbase settings (read /
write split with shared cmd IDs). Read group has `set-` cmds removed and
returns currently-stored value of the corresponding write.

### Plugin status

Plugin does **not** drive these LEDs — no command wiring for any of the
above. If a future SimHub feature (e.g. "ambient base feedback") needs
them, send via `MozaDeviceManager.WriteSetting("base-led-...", value)`
once the commands are added to `MozaCommandDatabase`.
