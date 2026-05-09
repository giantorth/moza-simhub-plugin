### Type `0x81` — session channel open payload

`0x81` is the SerialStream **session-open** message type (analogous to TCP
SYN). Used by both host and device to allocate a session channel and
advertise an initial sequence number plus a receive-window credit. The peer
responds with a `fc:00` ACK echoing the open's sequence number.

There are two payload variants on the wire — host-initiated frames carry a
shorter form than device-initiated frames.

#### Host-initiated open (4-byte payload, type `0x81`)

```
7E 0A 43 17 7C 00 [session] 81 [port_lo] [port_hi] [port_lo] [port_hi] FD 02 [checksum]
                   └ chunk hdr ┘  └ open type     └ seq (LE) ┘  └ session_id (LE) ┘  └ window
```

| Offset | Bytes | Field | Notes |
|--------|-------|-------|-------|
| 0 | 1 | Frame start `0x7E` | |
| 1 | 1 | Length (`0x0A`) | Payload size after group/dev (10 bytes) |
| 2 | 1 | Group `0x43` | TelemetrySendGroup |
| 3 | 1 | Device `0x17` | Wheel |
| 4 | 1 | `0x7C` | SerialStream chunk header (data plane) |
| 5 | 1 | `0x00` | Chunk subtype |
| 6 | 1 | session byte | Local session identifier (e.g. `0x01`, `0x02`) |
| 7 | 1 | `0x81` | Type = session channel open |
| 8–9 | 2 | sequence (LE) | Initial sequence number; host echoes port number |
| 10–11 | 2 | session_id (LE) | Globally allocated port number; equals seq for host opens |
| 12–13 | 2 | window (LE) | `0xFD 0x02` = 765 byte receive window |
| 14 | 1 | Checksum | |

Plugin builder: [`SendSessionOpen`](../../../Telemetry/TelemetrySender.cs)
in `Telemetry/TelemetrySender.cs:1762`.

Example (plugin-issued session 0x01 mgmt open with port=1):

```
7E 0A 43 17  7C 00 01 81  01 00  01 00  FD 02  [chk]
```

#### Device-initiated open (6-byte payload, type `0x81`)

Device opens carry the same fields but in the **upstream** direction with
group `0xC3` and dev `0x71` (response transforms applied):

```
7E 0A C3 71 7C 00 [session] 81 [port_lo] [port_hi] [port_lo] [port_hi] FD 02 [checksum]
```

The port field is duplicated in every device-initiated open across all
captures (4 firmware variants checked). Port equals the session byte for
every device-opened session in 2025-11+ firmware (`0x04` → 4, `0x06` → 6,
`0x08` → 8, `0x09` → 9, `0x0A` → 10).

#### Receive-window field

`FD 02` (LE = 765) is constant across every observed open in every
direction. Treated as protocol literal — plugin and sim both emit it
verbatim.

#### Acknowledgment

The peer replies with a `fc:00` ACK echoing the open's `seq`:

```
7E 05 43 17 FC 00 [session] [seq_lo] [seq_hi] [checksum]
```

**The seq echo is required.** PitHouse maintains a monotonic port counter
across reconnects; replying with `ack_seq=0` (or any wrong value) causes it
to retry forever. See [`lifecycle.md`](lifecycle.md) for the port allocation
rule.

#### Concurrent opens

PitHouse routinely opens `0x01` and `0x02` in the same USB bulk transfer:

```
7E 0A 43 17 7C 00 01 81 01 00 01 00 FD 02 [chk]
7E 0A 43 17 7C 00 02 81 02 00 02 00 FD 02 [chk]
```

Both opens travel as a single concatenated USB write; the wheel ACKs each
in turn.
