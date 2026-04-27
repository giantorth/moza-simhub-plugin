## Heartbeat and keepalives

- **Group 0x00 heartbeat** — sent to every known device ID (18–30) ~1/s. Payload length 0. Keep-alive / presence check.
- **Group 0x43 bare keepalive** — bare `0x43` frames (n=1, payload=`0x00`) to devices 0x17/0x14/0x15 every ~1.1s. Device replies `0x80`. Connection-level ping.
- **Group 0x43 broadcast** — length=2 packets to dash (0x14) and device 0x15 every ~5s. Heartbeat/sync.

### Unsolicited messages

- **Group 0x0E** from wheel (device 23): ASCII debug/log text, ~every 2s. NRF radio stats, e.g. `NRFloss[avg:0.00000%] recvGap[avg:4.70100ms]`.
- **Group 0x06** from wheel (device 23): 12-byte hardware identifier. In `connect-wheel-start-game.json` host-initiated (part of probe), not purely unsolicited. VGS response: `be 49 30 02 14 71 35 04 30 30 33 37`.
