## Complete telemetry startup timeline

Two captures provide complementary views.

### Concurrent outbound streams during active telemetry

| Stream | Rate | Device | Group/Cmd | Purpose | Required? |
|--------|------|--------|-----------|---------|-----------|
| Sequence counter | ~45/s | base (0x13) | `0x2D/F5:31` | Frame sync to base | TBD |
| Telemetry enable | ~48/s | wheel (0x17) | `0x41/FD:DE` data=`00:00:00:00` | Mode/enable flag | Likely — entire session |
| **Live telemetry** | ~31/s | wheel (0x17) | `0x43/7D:23` | Bit-packed game data | Yes |
| Heartbeat | ~1/s each | all devices (18–30) | `0x00` n=0 | Keep-alive / presence | Likely |
| RPM LED position | ~4/s | wheel (0x17) | `0x3F/1A:00` | LED bar position | Separate feature |
| Telemetry mode | ~3/s | wheel (0x17) | `0x40/28:02` data=`01:00` | Set/poll multi-channel mode | Likely |
| Dash keepalive | ~1.5/s | dash (0x14), 0x15, wheel (0x17) | `0x43` n=1, data=`00` | Keep-alive for dash and wheel sub-devices | Yes — Pithouse sends to all three |
| Display config | ~1/s | wheel (0x17) | `0x43/7C:27` | Page-cycled display params | Yes |
| Dashboard activate | ~1/s | wheel (0x17) | `0x43/7C:23` | Declares active dashboard pages | Yes |
| Status push | ~1/s | wheel (0x17) | `0x43/FC:00` | Session ack with session=FlagByte and current ack seq (NOT zeros) | Yes — Pithouse uses real session/seq |
| Settings block | ~1/s | wheel (0x17) | `0x43/7C:00` | Config sync | No (file transfer) |
| Button LED | ~1/s | wheel (0x17) | `0x3F/1A:01` | Button LED state | Separate feature |

### Preamble detail — from `moza-startup.json` (2026-04-12, raw Wireshark JSON)

Most precise source, decoded directly from raw USB packets:

| Offset | Frame | Notes |
|--------|-------|-------|
| +0.000 | `7c:00` type=0x81 session 0x01 + 0x02 | Opens two SerialStream sessions simultaneously |
| +0.009 | (IN) `fc:00` acks for both sessions | Wheel accepts immediately |
| +0.013 | (IN) `7c:00` data on session 0x02 | Wheel dumps channel registrations (v1/gameData/Rpm etc.) |
| +0.053-0.087 | `fc:00` acks (seq 04→17) | Host acks each incoming data chunk |
| +0.064-0.070 | `7c:00` tier definition TO wheel | Host sends tier config (channel indices, compression codes, bit widths) |
| +0.072 | First `7d:23` telemetry (flag=0x00) | Interleaved with acks — smaller "probe" tier, n=14 |
| +0.100-1.000 | `7d:23` flag=0x00 (~25 frames) | ~30Hz, heartbeats only — no 0x41 enable yet |
| +0.700-0.970 | Identity probes to wheel/base/pedals | Groups 0x00, 0x02-0x11 |
| +0.970 | **`0x0E` debug poll starts** | Parameter table reads at ~9Hz to 0x12/0x13/0x17 |
| +1.054 | **First `0x41/FD:DE` enable** | 1.05s after session opens |
| +1.089 | `0x40` channel config (1E, 09:00) | Deferred until after session exchange |
| +1.124-1.127 | `7c:00` additional config on session 0x02 | Second batch of tier data |
| +1.130 | **First `7d:23` with flag=0x02** (n=24) | Full telemetry — session exchange complete |
| +1.200 | Display sub-device probe | Identity commands via 0x43 (model="Display") |

### Full connect-to-telemetry — from `connect-wheel-start-game.json`

Wheel plugged in cold, then Assetto Corsa started:

| Phase | Time | Events |
|-------|------|--------|
| **Idle** | t=0–7.8s | Heartbeats, keepalives, `0x0E` debug poll. Only dev18/19/23 respond |
| **Wheel detected** | t=7.82s | Identity probe: 0x09 → 0x04 → 0x06 → 0x02 → 0x05 → 0x07 → 0x0F → 0x11 → 0x08 → 0x10 |
| **Config burst** | t=8.2–9.1s | ~50 `0x40` commands (channel enables, page config, LED config). `0x40/28:02` polling at ~3 Hz |
| **Dashboard upload** | t=21.4–23.5s | `0x43/7c:00` chunked file transfer. Display sub-device probed |
| **Pre-game** | t=24–30.5s | `0x40/28:02` polling (response always `00:00`), heartbeats, keepalives |
| **Game starts** | t=30.568s | `0x41/FD:DE` enable + `0x2D/F5:31` seq counter start simultaneously |
| **Telemetry** | t=30.600s | `0x43/7D:23` live data (flag=0x02). ~31 frames/s steady state |
