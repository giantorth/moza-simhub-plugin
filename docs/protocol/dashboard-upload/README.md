# Dashboard upload protocol

`.mzdash` file uploads + dashboard config RPCs. Three upload-protocol variants observed across firmware generations.

## Firmware version matrix

| Firmware | Upload session | Sub-msg types | Paths marker | File |
|----------|----------------|---------------|--------------|------|
| 2026-04 (legacy, `09-04-26/dash-upload.pcapng`) | Session 0x01 mgmt | FF-prefix envelopes, 3 fields | n/a (binary fields) | [`path-a-session-01-ff.md`](path-a-session-01-ff.md) |
| 2025-11 (latestcaps) | Session 0x04 | type=0x02/0x03 host, 0x01/0x11 wheel | `0x8A` LOCAL | [`path-b-session-04.md`](path-b-session-04.md) |
| 2026-04+ (current PitHouse) | Session 0x05 or 0x06 (dynamic via `7c:23` trigger) | type=0x02/0x03 host, 0x01/0x11 wheel | `0x8C` LOCAL | [`upload-handshake-2026-04.md`](upload-handshake-2026-04.md) |

**Plugin** currently implements Path B (session 0x04, `0x84`/`0x8C` markers, BE sizes). Sim implements the 2026-04+ variant (dynamic session, `0x8C` marker). For current PitHouse builds, see [`upload-handshake-2026-04.md`](upload-handshake-2026-04.md) — Paths A and B are retained for historical firmware compatibility but neither matches current wire behavior.

## Files

| File | Topic |
|------|-------|
| [`path-a-session-01-ff.md`](path-a-session-01-ff.md) | Path A — session 0x01 host-initiated FF-prefix upload (legacy 2026-04, plugin impl) |
| [`path-b-session-04.md`](path-b-session-04.md) | Path B — session 0x04 device-initiated sub-msg 1/2 (2025-11) |
| [`session-04-root-dir.md`](session-04-root-dir.md) | Session 0x04 device → host root directory listing (2025-11 firmware) |
| [`config-rpc-session-09.md`](config-rpc-session-09.md) | Dashboard config RPC (session 0x09, compressed transfer) |
| [`session-01-mgmt-rpc.md`](session-01-mgmt-rpc.md) | Session 0x01 management RPC envelope |
| [`upload-handshake-2026-04.md`](upload-handshake-2026-04.md) | 2026-04+ firmware upload handshake: full sequence, `ff*4` sentinel + 1-byte XOR status, multi-round zlib |
| [`6-byte-submsg-header.md`](6-byte-submsg-header.md) | New-firmware 6-byte sub-msg header (2026-04+) |
| [`per-chunk-trailer.md`](per-chunk-trailer.md) | Per-chunk metadata trailer (continuation chunks) |
| [`multi-attempt-interleaving.md`](multi-attempt-interleaving.md) | Multi-attempt upload interleaving in the buffer |

Underlying transport: [`../sessions/`](../sessions/).
