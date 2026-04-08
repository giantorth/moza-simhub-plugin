# Moza Racing serial connection protocol

### Table
<table>
<thead>
<tr>
<th colspan=4>Header</th>
<th colspan=2>Payload</th>
<th rowspan=2>Checksum</th>
</tr>
<tr>
<th>Start</th>
<th>Payload length</th>
<th>Request group</th>
<th>Device id</th>
<th>Command id</th>
<th>Value(s)</th>
</tr>
</thead>
<tbody>
<tr>
<td>0x7e</td>
<td>1 byte</td>
<td>1 byte</td>
<td>1 byte</td>
<td>1+ byte</td>
<td>n bytes</td>
<td>1 byte</td>
</tr>
</tbody>
</table>

If a command id is an array of integers, you must provide them sequentially in the same order

Values are transmitted in big-endian.

### Checksum calculation
Checksum is the reminder of sum of the bytes divided by 0x100 (mod 256)
ChecksumByte8mod256

This sum includes the USB device serial endpoint (always 0x02), type (URB_BULK -> 0x03) and probably
the whole message lenght (typically 0x08), although this could be a bug in Moza Firmware, as even with longer messages, changing this last part of the "magic value" causes devices to not respond.

**Magic value = 13 (0x0d)**

### Responses

**Request group** in response has `0x80` added, so when reading request group `0x21` we should expect a response group of `0xa1`. The MSB indicates response direction.

**Device id** has its byte halves swapped. When reading/writing to device `0x13 (base)`, response will contain device `0x31` and so on.

**Payload length** in the response reflects the data the device sends back, not the request payload length. For write requests the response payload mirrors the request. For read requests the response payload contains the current stored value regardless of how many bytes the request sent — in practice this means a minimal read probe (e.g. payload = just the command ID, 1 byte) will receive a full-length response (e.g. 16 bytes of string data).

Checksum calculation is the same as for requests.

### Devices and commands
The list of device ids and command data can be found in the [serial.yml](./data/serial.yml) file.

### Command chaining
You can send multiple commands at once. The device sends back all responses, but **not necessarily in the same order as the requests**. Responses are matched to requests by group number, not by position in the stream.

### Unsolicited messages
Some devices emit packets without a corresponding request. Observed examples:

- **Group 0x0E** from the main device (device 18): firmware debug/log text as ASCII. Filtered in practice.
- **Group 0x06** from the wheel (device 23): emitted spontaneously on connection, ~12 bytes, contains what appears to be a partial hardware identifier. Purpose unknown.

### Wheel connection probe sequence

When a wheel is detected, Pit House sends the following identity queries to device 0x17 (wheel, ID 23). Responses arrive asynchronously, matched by group. All identity strings are 16-byte null-padded ASCII.

| Group | Cmd ID | Response content | Notes |
|-------|--------|-----------------|-------|
| 0x02 | — | 1-byte value (observed: `0x02`) | Unknown — possibly protocol version |
| 0x04 | `0x00` + 3 zero bytes | 2 bytes | Unknown |
| 0x05 | `0x00` + 3 zero bytes | 4 bytes, differs per model | Possibly capability flags or button/LED count. VGS: `01 02 1f 01`; CS V2.1: `01 02 26 00` |
| 0x07 | `0x01` | 16-byte string | **Model name** — e.g. `VGS`, `CS V2.1`, `R5 Black # MOT-1` |
| 0x08 | `0x01` | 16-byte string | **Hardware version** — e.g. `RS21-W08-HW SM-C` |
| 0x08 | `0x02` | 16-byte string | **Hardware revision** — e.g. `U-V12`, `U-V02` |
| 0x0f | `0x01` | 16-byte string | **Firmware (SW) version** — e.g. `RS21-W08-MC SW` |
| 0x10 | `0x00` | 16-byte string | **Serial number, first half** |
| 0x10 | `0x01` | 16-byte string | **Serial number, second half** |
| 0x11 | `0x04` | 2 bytes | Unknown |

The full serial number is the two halves concatenated (32 ASCII characters). Observed across wheel models: R5 Black (old protocol, ES series) also responds correctly to all of these.

For identity read requests, the request payload is just the command ID byte with no value bytes appended. The device responds with 16 bytes regardless.

### Telemetry color chunks (wheel LED effects)

RPM and button LED colors are sent via the `wheel-telemetry-rpm-colors` and `wheel-telemetry-button-colors` commands. Each command has a fixed payload size of 20 bytes, so colors are split into 20-byte chunks and sent as multiple consecutive writes.

Each LED is encoded as 4 bytes:

| Byte | Meaning |
|------|---------|
| 0 | LED index (0-based) |
| 1 | Red |
| 2 | Green |
| 3 | Blue |

Five LEDs fit per chunk (5 × 4 = 20 bytes). With 10 RPM LEDs this is exactly 2 chunks. With 14 button LEDs it is 3 chunks, where the last chunk only contains 4 real LEDs (16 bytes) and must be padded to 20 bytes.

**Padding caveat:** zero-padding produces `[0x00, 0x00, 0x00, 0x00]`, which the firmware interprets as a valid entry: *set LED index 0 to RGB(0, 0, 0)*. This overwrites the correct color already set in the first chunk and causes button 0 to flicker black on every color send. The workaround is to use index `0xFF` for any unused padding entries, which the firmware ignores.
