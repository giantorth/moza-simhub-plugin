# SerialStream session protocol

Pit House transfers dashboard files, tier definitions, and RPCs using a proprietary TCP-like serial stream protocol (`MOZA::Protocol::SerialStreamManager`) over `0x43/7c:00`. `fc:00` carries acknowledgments. NOT CoAP — CoAP is a separate layer for device parameter management.

| File | Topic |
|------|-------|
| [`chunk-format.md`](chunk-format.md) | Chunk header, CRC algorithm, acknowledgments |
| [`lifecycle.md`](lifecycle.md) | Session open/close frames, port / session-byte allocation, concurrent session map (2025-11 vs 2026-04+ firmware) |
| [`compressed-0x09-0x0a.md`](compressed-0x09-0x0a.md) | Compressed transfer format (sessions `0x09`, `0x0a`) and cumulative-ack heartbeat |
| [`session-0x01-channel-protocol.md`](session-0x01-channel-protocol.md) | Session `0x01` typed sub-msg framing: catalog (`type=0x04`), tier-def (`type=0x01`), string value push (`type=0x05`), acks (`type=0x06`), init (`type=0x07`) — parallel to the sess=0x02 protocol, not a replacement |
| [`session-0x02-ff-init.md`](session-0x02-ff-init.md) | Session `0x02` FF-record init handshake (kind=2/7/8/11), wheel-side ack (kind=10/16), and the verified-broken shortcut of replaying captured kind=8/11 bytes |
| [`session-0x03-tile-envelope.md`](session-0x03-tile-envelope.md) | Session `0x03` tile-server envelope variant (12 bytes) |
| [`type-0x81-channel-open.md`](type-0x81-channel-open.md) | Type `0x81` session-channel-open payload |
| [`session-0x0a-rpc.md`](session-0x0a-rpc.md) | Session `0x0a` RPC (host → device) |

Application layers built on top of sessions: [`../tier-definition/`](../tier-definition/) (uses sessions 0x01/0x02), [`../dashboard-upload/`](../dashboard-upload/) (uses sessions 0x01/0x04/0x09/0x0a), the sess=0x01 channel protocol (catalog + tier-def + string values — see [`session-0x01-channel-protocol.md`](session-0x01-channel-protocol.md)), and host→wheel property pushes for the wheel-integrated dashboard (uses session 0x02; see [`session-0x02-ff-init.md`](session-0x02-ff-init.md) for the init handshake and the [`../findings/2026-04-29-session-01-property-push.md`](../findings/2026-04-29-session-01-property-push.md) note for the inner FF-record envelope — the file name retains the historical session-01 hypothesis since superseded by the sess=0x02 finding).
