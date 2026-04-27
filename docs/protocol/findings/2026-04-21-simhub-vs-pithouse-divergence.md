## SimHub plugin vs PitHouse wire divergence (2026-04-21)

Side-by-side capture with both clients connected to independent wheel_sim instances (SimHub on `/dev/tnt0`, PitHouse on `/dev/ttyGS0`, VGS model). Frames observed by sim = frames **sent by client**.

> **Canonical topical homes for facts in this journal:**
>
> This file is mostly journal-only — it's a divergence audit comparing two clients against the same sim. Most H3s describe SimHub-specific behaviour or document gaps the plugin still has. For protocol-layer facts that came out of this audit:
>
> | H3 section | Now also documented at |
> |------------|------------------------|
> | Probe frame differences | [`../identity/wheel-probe-sequence.md`](../identity/wheel-probe-sequence.md) |
> | Items likely worth fixing in SimHub | [`../plugin/`](../plugin/) |
> | Phase 3 — dashboard/config upload blobs (NOT implemented) | [`../dashboard-upload/`](../dashboard-upload/) |
> | Session 0x09 configJson burst timing | [`../sessions/compressed-0x09-0x0a.md`](../sessions/compressed-0x09-0x0a.md) |

### Functional split (expected divergence)

PitHouse is dashboard/config manager. SimHub plugin is telemetry feeder. Each owns distinct protocol paths; neither does full set.

| Behaviour | PitHouse (42s window) | SimHub (746s window) | Notes |
|-----------|----------------------|----------------------|-------|
| Telemetry frames pushed (0x43/7D23) | 0 | 18501 | SimHub-only job |
| `0x41/FDDE` dash-telemetry-enable (~100 Hz) | 0 | 17473 | SimHub-only |
| `0x2D/F531` base sequence counter (~50 Hz) | 0 | 17335 | SimHub-only |
| Uploaded zlib blobs (tile_server maps + UTF-16 display strings) | 5 (sessions 0x02, 0x03) | 0 | PitHouse-only — config push |
| Catalog frames back from wheel (`catalog_sent`) | true | false | PitHouse triggers wheel to return channel catalog; SimHub never requests |
| Identity handshake frames | 7 | 0 | PitHouse probes wheel identity every connect; SimHub skips |
| `fw_debug` subscription (group 0x0E dev 0x17) | 522 frames (incrementing seq) | 0 | Diagnostic-only. SimHub correctly skips |

### Probe frame differences

| Probe | PitHouse | SimHub plugin | Documented? |
|-------|----------|---------------|-------------|
| Base probe (group 0x2B dev 0x13) | `7E 03 2B 13 02 00 00 CE` | `7E 03 2B 13 01 00 01 CE` (pre-fix) → now `02 00 00 CE` | FIXED 2026-04-21: `BaseProbeFrame` in `MozaSerialConnection.cs:469` now matches PitHouse pattern |
| Hub probe (group 0x64 dev 0x12) | not sent | `7E 03 64 12 03 00 00 07` | `(0x64, 0x12, 03)` documented in `sim/wheel_sim.py` as "hub-port1-power probe". PitHouse does not use — SimHub plugin-specific |

### Periodic polling not done by PitHouse

SimHub plugin sends these ~0.36 Hz (panel-timer-gated, only fires while settings panels visible):

| Frame | Sim label |
|-------|-----------|
| `7E .. 40 15 1C 00 ..` | wheel settings read cmd=1c 00 dev=0x15 |
| `7E .. 40 15 18 00 ..` | wheel settings read cmd=18 00 dev=0x15 |
| `7E .. 40 13 1C 00 ..` | wheel settings read cmd=1c 00 dev=0x13 |
| `7E .. 40 13 18 00 ..` | wheel settings read cmd=18 00 dev=0x13 |
| `7E .. 5B 1B 01 00 ..` | handbrake-direction probe |
| `7E .. 23 19 01 00 ..` | pedals-throttle-dir probe |

Wheel sim tags as unhandled. Plugin-side settings-panel polls (in `Devices/` code paths). No PitHouse equivalents during normal session.

### One-shot writes unique to SimHub

Seen once each in 746 s, tagged unhandled by sim — plugin issued, no PitHouse counterpart:

- `0x1F 0x12 cmd 33 00` / `0x1F 0x12 cmd 08 00` — main hub settings
- `0x3F 0x17 cmd 04 01` / `0x3F 0x17 cmd 07 00` / `0x3F 0x17 cmd 14 00` — wheel RPM/LED telemetry reads
- `0x33 0x14 cmd 0b/0a/07/11` variants + `0x32 0x14 cmd 0a/0b/11` variants — dash settings reads/writes

Most trace to per-device settings controls under `Devices/` exercised when wheel is first detected.

### Session counts

| Metric | PitHouse (142 s) | SimHub (746 s) | Rate ratio |
|--------|-----------------|----------------|-----------|
| `session_open` count | 3 | 7 | SimHub opens more sessions over time |
| `session_end` count | 9 | 33 | Both reset sessions repeatedly; SimHub higher |
| `proactive_sent` (sim→client) | 415 (~2.9/s) | 812 (~1.1/s) | Sim fires more proactive opens at PitHouse — suggests SimHub not fully acking or not driving state that causes wheel to open more |
| `session_ack_in` | 41 | 797 | SimHub acks much more traffic (reflects telemetry volume) |

### What SimHub is missing vs PitHouse

If dashboard-management parity with PitHouse is ever a goal:

1. **Identity handshake** — SimHub sends Display sub-device probe via 0x43 (`SendDisplayProbe`) but previously did not send the 7 top-level PitHouse-style identity probes (direct groups 0x09/0x02/0x04/0x05/0x06/0x08sub2/0x11 to dev 0x17). PitHouse fires all 12 frames (7 direct + 5 via wheel-model-name/sw-version/hw-version/serial-a/serial-b) on every connect. **FIXED 2026-04-21**: `MozaDeviceManager.SendPithouseIdentityProbe(deviceId)` added. Fires 7 PitHouse direct-group identity frames not covered by existing `ReadSetting` calls: `0x09` (presence/ready), `0x02` (device presence), `0x04` (device type), `0x05` (capabilities), `0x06` (hardware ID), `0x08 cmd=02` (HW sub-version), `0x11 cmd=04` (identity-11). Called at wheel-detection point in `MozaPlugin.cs` for both new-protocol and old-protocol branches. Brings SimHub to 12-frame PitHouse identity parity on connect.
2. **Wheel-returned channel catalog** — SimHub never triggers wheel to send channel catalog back. `catalog_sent=false` in sim status. PitHouse completes this exchange within seconds.
3. **Config blob upload on session 0x02/0x03** — PitHouse uploads UTF-16 display-string tables and tile-server map JSON during startup. SimHub uploads none. See Phase 3 below.
4. **fw_debug / EEPROM parameter reader** — diagnostic only, can stay unimplemented.

### Items likely worth fixing in SimHub

- **Periodic settings polls** (not a bug): 0.36 Hz rate observed was panel-timer-gated — `MozaWheelSettingsControl` uses `DispatcherTimer(500ms)` started on `Loaded`, stopped on `Unloaded`. Similar gating in `MozaDashSettingsControl`. No fix needed; polls only flow while panels visible.
- **One-shot dash-settings writes on connect** (not a bug): legitimate UI initialisation triggered by `MozaDashSettingsControl.RefreshDash()` on first panel open. Reads current state, writes defaults if unset.

### Phase 3 — dashboard/config upload blobs (NOT implemented in SimHub)

PitHouse uploads 5 zlib-compressed blobs during connect that SimHub does not send. These populate wheel's native dashboard UI (channel-name lookup tables + map tiles). SimHub is telemetry feeder, not dashboard manager, so telemetry works without them. Implementation would be ~multi-day RE with limited benefit unless SimHub takes over "native wheel UI" role.

**Session 0x02, blob 1 (~7.2 KB)** — channel-name dictionary
- UTF-16LE strings, length-prefixed, tagged entries
- Content observed: `RpmAbsolute1..10`, `RpmPercent1..N`, similar telemetry-channel display names alphabetical
- Each entry appears `[tag_u16_le] [string_id_u16_le] [utf16_len_u16_le] [utf16le_bytes] [null_u16]` — exact layout not fully reversed
- Preamble: 59-byte offset (matches PitHouse FF-prefixed upload framing)
- Purpose: lets wheel's dashboard UI render channel names without embedding them in firmware; supports localisation and channel additions without FW updates

**Session 0x02, blob 2 (~9.9 KB)** — input-action-name dictionary
- Same UTF-16LE tagged layout
- Content observed: action/command names — `decrementEqualizerGain1..6`, `decrementGameForceFeedbackFilter`, similar wheel-action identifiers
- Purpose: label pickable actions in wheel's button-binding UI

**Session 0x03, blob 1 (~775 B)** — empty tile-server map metadata
- zlib-compressed JSON: `{"map":{"ats":"<inner_json_string>","ets2":"<inner_json_string>"},"root":"...","version":...}`
- Inner `ats`/`ets2` values JSON-escaped strings with fields `bg, ext_files, file_type, layers, levels, map_version, name, pm_support, pmtiles_exists, root, support_games, tile_size, version, x_max/min, y_max/min`
- Empty/default state: all zero / empty arrays. PitHouse sends when no map tiles installed
- `root` field is host-side tile-server path (e.g. `C:/Users/giant/AppData/Local/Temp/tile_server/ats`)

**Session 0x03, blobs 2+3 (~3 KB and ~6.3 KB)** — populated map metadata
- Same JSON schema as blob 1
- `layers` array populated with per-zoom-level tile counts, `ext_files: ["cities.json","file_map.json"]`, `file_type: "png"`, `bg: "#ff303030"`
- Sent once tile data exists on host for respective game (ATS or ETS2)
- Purpose: wheel's integrated map display for American Truck / Euro Truck Simulator 2; driven from PitHouse's local tile_server directory

**Decision (2026-04-21)**: Deferred. Needs:
1. Full tagged-UTF-16 binary layout reversed (bit-level) from multiple captures
2. Complete channel-name + action-name enumeration (can source from `rs21_parameter.db` for action names; channel names from `Telemetry.json`)
3. Tile-server JSON schema lock-in (mostly visible already, but `version` field semantics unclear)
4. Wiring into existing `Dashboard upload protocol` framing (session 0x02 / 0x03 chunked writes with CRC-32 per chunk)

Punt until concrete use case for SimHub driving wheel's native dashboard UI. `.mzdash` upload path (already implemented on session 0x01) covers dashboard-body case; these blobs purely cosmetic UI metadata on top.

### Phase 4 — fw_debug subscription (NOT implemented, intentional)

PitHouse subscribes to `group 0x0E dev 0x17` and receives ~522 incrementing-seq debug frames per session. Content is ASCII firmware log output. Diagnostic-only — not required for telemetry. Skipping is correct for SimHub.

### Session 0x09 configJson burst timing (real wheel vs sim)

Measured on `usb-capture/latestcaps/automobilista2-wheel-connect-dash-change.pcapng` (VGS + 2025-11 firmware). Real wheel's device→host session 0x09 state push on connect:

| Metric | Real wheel | wheel_sim (chunk_size=54 fix) |
|--------|------------|-------------------------------|
| Total chunks | 32 (seq 0x000A–0x0029) | 8 (seq 0x0100–0x0107) |
| Wire N per chunk | 64 | 64 |
| Net data per chunk | 54 B + 4 B CRC | 54 B + 4 B CRC |
| Total window | ~90 ms | ~40 ms |
| Avg inter-chunk gap | ~3 ms | ~6 ms |
| Min inter-chunk gap | 0.0 ms (same USB microframe) | ~6 ms |

**Real hardware bursts FASTER than the sim**, so any plugin that keeps up on real HW will also keep up with sim. In production (native Windows + USB URB bulk transfers), the plugin receives consolidated byte blocks from kernel-side URB completions and parses cleanly.

#### Chunk body size — wire N field must stay ≤ 64

Real-wheel session 0x09 chunks carry N=64 on wire: 6-byte `7C:00:sess:01:seqL:seqH` header + 54-byte net payload + 4-byte per-chunk CRC32 trailer = 58-byte frame body + group/device = N=64. Confirmed by decoding `automobilista2-wheel-connect-dash-change.pcapng` chunks: strip trailing 4 bytes per chunk + concat → valid zlib stream (comp=1709, uncomp=7231).

Frame length field N is a single byte (0–255), but the plugin's framer enforces `payloadLength > 64 → reject` (`Protocol/MozaSerialConnection.cs:237`) to match the observed real-wheel upper bound. Any chunker that emits N>64 trips the reject path: the 0x7E at the start is consumed, cursor advances by one byte, and the framer resyncs byte-by-byte through the chunk body. When it hits a stray 0x7E inside the compressed payload it will attempt a bogus parse, log a single `DROP checksum mismatch` with nonsense group/device, and keep resyncing until it reaches the next valid frame start — which is usually the short final chunk of the same burst (often just under N=64 by accident).

Earlier `chunk_session_payload(chunk_size=58)` default in `sim/wheel_sim.py` produced N=68 (58 net + 4 CRC + 6 header = 68). Under that setting session 0x09 bursts reliably lost 6 of 7 chunks on both Linux (tty0tty) and Windows (USB CDC gadget → SimHub / PitHouse). Identical DROP byte pattern `3E-62-A5-A3-E1-79-03-99` appeared on both systems, proving the bytes arrived but the framer rejected each N=68 frame silently. Fix (2026-04-22): default `chunk_size=54` so chunk body + CRC + header totals N=64, matching real wheel. After the fix, all 8 session-0x09 chunks and all 4 session-0x04 dir-listing chunks parse cleanly in the SimHub plugin, and PitHouse Windows ingests the configJson state without issue.

#### KS Pro / 2026-04+ firmware — configJson migrated to session 0x0a (no CRC)

KS Pro firmware moved the configJson state push from session **0x09** to **0x0a**:

| Aspect | VGS/CSP (0x09) | KS Pro (0x0a) |
|--------|----------------|---------------|
| Session role 0x09 | configJson state push | empty heartbeats only |
| Session role 0x0a | rare RPC channel | **configJson state push + host configJson() reply** |
| Session role 0x03 | tile-server state | (host pushes on 0x0b instead) |
| Net data per chunk | 54 B + 4 B CRC32-LE trailer | 54 B, **no CRC trailer** |
| Wire N per chunk | 64 (54 + 4 + 6 header) | 60 (54 + 6 header) |
| First device-side seq | `0x000a` (port + 1) | `0x000b` (port + 2) |
| Factory dashboards | 11 (Rally V1..V6 + Core/Mono/Pulse/Nebula/Grids) | **10** (NO Nebula) |

Decoded byte-exact 2026-04-26 from `usb-capture/ksp/mozahubstartup.pcapng` seq 11..69 (envelope `00 [comp:4 LE] [uncomp:4 LE]` + zlib stream, comp=3171 uncomp=14671). Reassembling with `crc_bytes=4` truncated each chunk's last 4 zlib bytes and `zlib.decompress` returned `Error -3 invalid bit length repeat`; switching to `crc_bytes=0` yields a clean zlib stream that decompresses to a valid configJson JSON.

Sim drives the variant via `WHEEL_MODELS[<model>]['configjson_session']` (default `0x09`, KS Pro overrides to `0x0a`). `chunk_session_payload(..., crc_bytes=0)` selects the no-CRC variant. KS Pro factory dashboard set captured into `sim/factory_state_kspro.json`; substituting `factory_state_w17_rgb.json` (CSP's 11-dashboard set) leaves PitHouse refusing the state on KS Pro despite the chunks decoding correctly — the firmware-baked dashboard list itself is part of the cache key.

Symptom of pushing on the wrong session / format: PitHouse responds with a 6-byte payload `7c 00 0a 00 [seq_lo] [seq_hi]` (type=0x00 with 6-byte body, NOT seen in any working real-wheel pcap) and treats the wheel as cache-stale, displaying whatever dashboards it last recorded. Verified 2026-04-26 against KS Pro on R12 base.

#### Session 0x0a fc:00 cumulative-ack heartbeat is mandatory

For KS Pro firmware (configjson_session=0x0a) the wheel must emit a 3-byte `fc 00 0a` cumulative-ack frame as a periodic heartbeat. Real-wheel cadence is ~4 s (verified `mozahubstartup.pcapng` t=3.205 / 7.209 / 11.211 — three frames before any host activity on 0x0a). Sim emits one alongside each session 0x09 keepalive (~2 s).

Without it PitHouse never sends its own `fc 00 0a [ack_seq:u16]` reply, treats session 0x0a as not-yet-ready, and discards any state push that arrives — verified 2026-04-26: sim chunked + pushed state cleanly but PitHouse showed an empty wheel and would not switch the active dashboard. Adding the heartbeat alone (no other changes to push timing or wire format) made PitHouse parse the state, populate the dashboard list, and accept active-dash switches via `28:00`.

Frame: `7e 03 c3 71 fc 00 0a [csum]` — note 3-byte payload, distinct from the 5-byte session-open ack `fc 00 [sess] [ack_lo] [ack_hi]`. The 5-byte form is what PitHouse sends back to acknowledge specific seqs once it considers the session alive.
