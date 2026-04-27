## Other periodic commands

### Group 0x0E parameter table reader / debug console (host → devices 0x12/0x13/0x17, ~9 Hz)

Pithouse sends 158 per session. Host reads EEPROM parameters sequentially and receives firmware debug log output.

**Request format:** `7E 03 0E [device] 00 [table] [index] [checksum]`
- `table`: EEPROM table number (0x00 = base config, 0x01 = alt)
- `index`: parameter index, incremented sequentially (0x01, 0x03, 0x04, ...)

**Response format (group 0x8E):**
- **Parameter values** (cmd=00:00, n=7): `[index] 00 00 [value bytes]` — stored parameter at index
- **Debug log text** (cmd=05:xx, variable length): ASCII firmware log output, e.g.:
  - `"RFloss[avg:0.00000%] recvGap[avg:4.25699ms]"` — NRF radio stats
  - `"INFO]param_manage.c:340 Table 2, Param 43 Written: 0"` — EEPROM write confirmation

Debug log entries confirm `0x40/1E` channel config commands write to EEPROM. Diagnostic only — **not required for telemetry**.

Starts ~1s after session opens. Sent to base (0x12, 51 frames), wheel (0x17, 68 frames), pedals (0x13, 39 frames). Plugin does not implement.

Short-form host poll also sent ~1 Hz to device 0x13: 3-byte payload `00 01 XX` with 16-bit BE countdown counter starting at 0x013A (314). Base echoes back + 4 unknown bytes.
