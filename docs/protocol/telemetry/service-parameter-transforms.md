## Device and command reference

See [`../devices/`](../devices/) for the full device ID and per-device command tables.

### Authoritative source: rs21_parameter.db

Pit House installation contains `bin/rs21_parameter.db` — SQLite DB with 919 commands across 23 groups. Canonical reference for RS21 (sim racing) device commands: names, descriptions, request/response group encoding, payload sizes, data types, valid ranges, EEPROM addresses. `request_group` field encodes as JSON array: first element = protocol group byte, remaining elements = command ID bytes. Example: `[40, 2]` → group 0x28, cmd 0x02.

Commands NOT in DB (discovered via USB captures): identity queries (groups 7/8/15/16), music sub-commands (group 42), sequence counter (group 45), telemetry enable (group 65), live telemetry stream (group 67/0x43).

## ServiceParameter value transforms (rs21_parameter.db)

`ServiceParameter` table documents how raw **device setting** values (groups 31–100) map to display units. Separate from telemetry encoding above — applies to Pit House settings UI, NOT telemetry bit stream.

| Function | Params | Example | Meaning |
|----------|--------|---------|---------|
| `multiply` | `0.01` | FFB strength 0–10000 → 0–100% | Raw × 0.01 |
| `multiply` | `0.1` | Temperature raw → degrees | Raw × 0.1 |
| `multiply` | `0.05` | Step values | Raw × 0.05 |
| `multiply` | `2` | Some parameters | Raw × 2 |
| `division` | `65535` | Normalize 16-bit | Raw / 65535 → 0.0–1.0 |
| `division` | `16384` | Normalize 14-bit | Raw / 16384 → 0.0–1.0 |
| `softLimitStiffness_conversion` | — | Soft limit stiffness | Custom non-linear |
