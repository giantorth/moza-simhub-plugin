## E-Stop (Device `0x1C` / 28)

Emergency-stop (kill switch) accessory. One read-only command on each of
two groups — read group `0xC6` (198) for unsolicited status pushes and
read group `0x46` (70) for explicit status polls.

> **Source:** rs21_parameter.db only — no E-stop captures available.
> Plugin does not implement an E-stop device extension.

### Command summary

| Command | Read Group | Cmd ID | Bytes | Type | Direction | Notes |
|---------|------------|--------|-------|------|-----------|-------|
| receive-status | `0xC6` (198) | `00` | 1 | int | dev → host (unsolicited) | Pushed by E-stop on state change |
| get-status | `0x46` (70) | `01` | 1 | int | host → dev (poll) | Returns current status byte |

### Frame layouts

**Unsolicited status push** (E-stop → host):

```
7E 02 C6 1C 00 [status] [chk]
```

| Byte | Value | Meaning |
|------|-------|---------|
| 0 | `0x7E` | Frame start |
| 1 | `0x02` | Payload length |
| 2 | `0xC6` | Group |
| 3 | `0x1C` | Device E-stop |
| 4 | `0x00` | Cmd ID |
| 5 | status | 1 = pressed, 0 = released (assumed; not yet captured) |
| 6 | chk | Frame checksum |

**Status poll** (host → E-stop):

```
7E 02 46 1C 01 00 [chk]
```

Response on group `0xC6` (= `0x46 | 0x80`) with dev `0xC1` (nibble swap):

```
7E 02 C6 C1 01 [status] [chk]
```

### Status byte semantics

The 1-byte status field's bit assignment is not yet decoded. The DB
declares it `int` (single byte) but no capture documents the value range
during press/release transitions. Treat as opaque until a capture confirms.

### Plugin status

Plugin has **no E-stop integration**. Adding one would require:

1. Adding the two commands to `MozaCommandDatabase` with the right
   group / cmd / type / direction.
2. Wiring an unsolicited-frame handler that recognises `0xC6 0x1C`
   responses and pipes status to a SimHub property.
3. Adding a periodic poll (group `0x46`) if devices don't reliably push
   status changes.

### See also

- [`../FIRMWARE.md`](../FIRMWARE.md) — firmware-era matrix; E-stop
  support has not been verified across firmware generations
- rs21_parameter.db `bin/rs21_parameter.db` — authoritative command list
