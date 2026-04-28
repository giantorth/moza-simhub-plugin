### Session management

Plugin manages two host-opened sessions and accepts every device-initiated
session that PitHouse-style firmware emits during connect. See
[`../sessions/lifecycle.md`](../sessions/lifecycle.md) for the firmware-era
session map and [`../sessions/type-0x81-channel-open.md`](../sessions/type-0x81-channel-open.md)
for the open frame layout.

### Host-opened sessions

| Session | Symbol / use | Open behavior |
|---------|--------------|---------------|
| `0x01` | Management — wheel identity, log push, channel catalog binary | Plugin opens with `type=0x81`; waits up to **500 ms** for `fc:00` ACK before proceeding |
| `0x02` | Telemetry — `FlagByte`, tier definition + FF-prefixed settings push | Same as 0x01; opens in same USB write |
| `0x03` | Aux — historical tile-server channel | **Fire-and-forget**: opened for doc compliance; plugin never writes here, but ACKs any unsolicited device data so wheel doesn't retransmit-stall |

Plugin builds `7E 0A 43 17 7C 00 [session] 81 [port] [port] FD 02 [chk]`
for each via [`SendSessionOpen`](../../../Telemetry/TelemetrySender.cs)
(line 1762). The two telemetry sessions are sent concurrently so the
wheel sees one USB packet with both opens.

### Device-initiated sessions

Wheel emits `type=0x81` opens during connect for `0x04`, `0x06`, `0x08`,
`0x09`, `0x0A` (older firmware) or `0x05`/`0x07`/`0x09`/`0x0A` (KS Pro on
Universal Hub). Plugin handler
[`OnMessageDuringPreamble`](../../../Telemetry/TelemetrySender.cs)
(line 1296) handles each:

```csharp
if (type == 0x81) {
    int openSeq = data[6] | (data[7] << 8);
    info.Port = (byte)(openSeq & 0xFF);
    SendSessionAck(session, (ushort)openSeq);
    if (session >= 0x04 && session <= 0x0a) {
        _ftCandidateSessions.Add(session);
        _uploadSessionOpened.Set();
    }
}
```

Key behaviors:

- **`fc:00` ACK echoes the open's `seq`** (`openSeq` from payload bytes
  6–7). Without this, PitHouse's monotonic port counter rejects the ACK
  as stale and retries forever.
- **Handler stays subscribed for the whole connection**, not just the
  ~1 s preamble window. Otherwise post-upload directory refreshes on
  `0x04`, configJson state pushes on `0x09`, and RPC replies on `0x0A`
  would be silently dropped.
- **Any device open in `0x04..0x0a` becomes a file-transfer candidate**;
  `ChooseUploadSession()` re-runs after each open to pick the right
  upload session for the current firmware (KS Pro can land on `0x05`,
  `0x06`, or `0x07` depending on the `7c:23` trigger).

### Stale-session reclaim

Before opening, plugin sends `type=0x00` end markers on ports `0x01..0x10`
to reclaim stale sessions left by a previous SimHub crash. Without this,
fresh opens are silently ignored — the wheel still considers the previous
host-port active and won't accept a new `type=0x81` for the same byte.

```
7E 06 43 17 7C 00 [session] 00 00 00 [chk]    # type=0x00 end marker
```

(Length byte must be 6; a length-6 frame with shorter payload causes
over-read into the next frame.)

### ACKs

`fc:00` is the SerialStream ACK type:

```
7E 05 43 17 FC 00 [session] [ack_seq_lo] [ack_seq_hi] [chk]
```

Plugin tracks ack state per session:

| Session | Ack tracking |
|---------|--------------|
| `FlagByte` (0x02) | `_sessionAckSeq` — bumped on every received `7c:00 type=0x01` chunk |
| `_mgmtPort` (0x01) | `_mgmtAckSeq` — same logic |
| `_uploadSession` | acks every chunk, plus message-count threshold for sub-msg-reply detection |
| `0x03` aux | unsolicited data ACKed verbatim |

ACK-seq is **highest contiguous seq received** (Stop-and-Wait); plugin
sends an ACK for every received chunk, not cumulatively.

### Source

[`Telemetry/TelemetrySender.cs`](../../../Telemetry/TelemetrySender.cs)
(`OnMessageDuringPreamble`, `SendSessionOpen`, `SendSessionAck`,
`SendSessionClose`).
[`Telemetry/SessionRegistry.cs`](../../../Telemetry/SessionRegistry.cs)
(`SessionRegistry`, `SessionInfo`).
