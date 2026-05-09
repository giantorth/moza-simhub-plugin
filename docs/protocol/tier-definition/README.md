# Tier definition protocol

Session 0x01/0x02 two-way handshake: wheel declares its channel catalog, host tells wheel how to decode incoming telemetry. TLV-encoded, transported as `7c:00` session data chunks.

| File | Topic |
|------|-------|
| [`handshake.md`](handshake.md) | Full bidirectional sequence from frame traces |
| [`session-01-device-desc.md`](session-01-device-desc.md) | Session 0x01 device description (both directions, both models) |
| [`session-02-channel-catalog.md`](session-02-channel-catalog.md) | Session 0x02 channel catalog (wheel → host) |
| [`version-0-url-csp.md`](version-0-url-csp.md) | Host response: version 0 URL subscription (CSP wheel) |
| [`version-2-compact-vgs.md`](version-2-compact-vgs.md) | Host response: version 2 compact tier definitions (VGS wheel) |
| [`tag-03-config-param.md`](tag-03-config-param.md) | Tag `0x03` config parameter |
| [`chunking.md`](chunking.md) | Chunking rules (both versions, both directions) |

Underlying transport: see [`../sessions/`](../sessions/).
