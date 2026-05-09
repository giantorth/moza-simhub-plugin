# Moza Racing serial protocol — REDIRECT INDEX

> **This file has moved.** The protocol reference has been split into a hierarchical layout under [`docs/protocol/`](protocol/README.md).
>
> Section anchors below are preserved (the original H2/H3 headers remain) so existing deep links from source code, sibling docs, and external bookmarks continue to resolve. Each section now contains a one-line pointer to its new home.
>
> **Status (2026-04-27):** Initial split complete. All sections have been moved verbatim into `docs/protocol/`. Each header below points to its new home. Inbound references from C# source, sibling docs, and external bookmarks continue to resolve to this file's anchors during the transition.

Start here: [`docs/protocol/README.md`](protocol/README.md).

---

## Frame format
→ [`protocol/wire/frame-format.md`](protocol/wire/frame-format.md)

### Checksum
→ [`protocol/wire/checksum.md`](protocol/wire/checksum.md)

### Checksum / body byte escape (0x7E byte stuffing)
→ [`protocol/wire/checksum.md`](protocol/wire/checksum.md)

### Responses
→ [`protocol/wire/frame-format.md`](protocol/wire/frame-format.md)

### Known wheel write echoes
→ [`protocol/wire/wheel-write-echoes.md`](protocol/wire/wheel-write-echoes.md)

### Command chaining
→ [`protocol/wire/frame-format.md`](protocol/wire/frame-format.md)

## USB topology
→ [`protocol/transport/usb-topology.md`](protocol/transport/usb-topology.md)

## Device and command reference
→ Per-device command tables: [`protocol/devices/`](protocol/devices/README.md). Authoritative source notes: [`protocol/telemetry/service-parameter-transforms.md`](protocol/telemetry/service-parameter-transforms.md).

### Authoritative source: rs21_parameter.db
→ [`protocol/telemetry/service-parameter-transforms.md`](protocol/telemetry/service-parameter-transforms.md)

## Device identity & probes
→ [`protocol/identity/`](protocol/identity/)

### Wheel connection probe sequence
→ [`protocol/identity/wheel-probe-sequence.md`](protocol/identity/wheel-probe-sequence.md)

### Display sub-device response table (wrapped in 0x43)
→ [`protocol/identity/display-sub-device.md`](protocol/identity/display-sub-device.md)

### Display sub-device (inside VGS wheel)
→ [`protocol/identity/display-sub-device.md`](protocol/identity/display-sub-device.md)

### Known wheel model names
→ [`protocol/identity/known-wheel-models.md`](protocol/identity/known-wheel-models.md)

### ES wheel identity caveat
→ [`protocol/identity/known-wheel-models.md`](protocol/identity/known-wheel-models.md)

## Heartbeat and keepalives
→ [`protocol/heartbeat.md`](protocol/heartbeat.md)

### Unsolicited messages
→ [`protocol/heartbeat.md`](protocol/heartbeat.md)

## Telemetry channel encoding
→ [`protocol/telemetry/channels.md`](protocol/telemetry/channels.md)

### Key constants
→ [`protocol/telemetry/channels.md`](protocol/telemetry/channels.md)

### Channel ordering
→ [`protocol/telemetry/channels.md`](protocol/telemetry/channels.md)

### Namespace distribution (Telemetry.json, 410 total channels)
→ [`protocol/telemetry/channels.md`](protocol/telemetry/channels.md)

## ServiceParameter value transforms (rs21_parameter.db)
→ [`protocol/telemetry/service-parameter-transforms.md`](protocol/telemetry/service-parameter-transforms.md)

## Live telemetry stream (group 0x43, device 0x17, cmd `[0x7D, 0x23]`)
→ [`protocol/telemetry/live-stream.md`](protocol/telemetry/live-stream.md)

### Frame structure
→ [`protocol/telemetry/live-stream.md`](protocol/telemetry/live-stream.md)

### Flag byte and multi-stream architecture
→ [`protocol/telemetry/live-stream.md`](protocol/telemetry/live-stream.md)

### Flag byte values across captures
→ [`protocol/telemetry/live-stream.md`](protocol/telemetry/live-stream.md)

### Example: F1 dashboard tier layouts
→ [`protocol/telemetry/live-stream.md`](protocol/telemetry/live-stream.md)

### Data verification (2026-04-12)
→ [`protocol/telemetry/live-stream.md`](protocol/telemetry/live-stream.md)

## Telemetry control signals
→ [`protocol/telemetry/control-signals.md`](protocol/telemetry/control-signals.md)

### Dash telemetry enable (group 0x41, device 0x17, cmd `[0xFD, 0xDE]`)
→ [`protocol/telemetry/control-signals.md`](protocol/telemetry/control-signals.md)

### Sequence counter (group 0x2D, device 0x13, ~50 Hz)
→ [`protocol/telemetry/control-signals.md`](protocol/telemetry/control-signals.md)

### RPM LED telemetry (group 0x3F, device 0x17, cmd `[0x1A, 0x00]`)
→ [`protocol/telemetry/control-signals.md`](protocol/telemetry/control-signals.md)

### LED group colour (group 0x3F, device 0x17, cmd `[0x27, <group>, <role>]`)
→ [`protocol/telemetry/control-signals.md`](protocol/telemetry/control-signals.md)

## Tier definition protocol (group 0x43, session data on 7c:00)
→ [`protocol/tier-definition/`](protocol/tier-definition/)

### Handshake sequence (from bidirectional frame traces)
→ [`protocol/tier-definition/handshake.md`](protocol/tier-definition/handshake.md)

### Session 0x01 — device description (both directions, both models)
→ [`protocol/tier-definition/session-01-device-desc.md`](protocol/tier-definition/session-01-device-desc.md)

### Session 0x02 — channel catalog (wheel → host, both models)
→ [`protocol/tier-definition/session-02-channel-catalog.md`](protocol/tier-definition/session-02-channel-catalog.md)

### Session 0x02 — host response: version 0 URL subscription (CSP)
→ [`protocol/tier-definition/version-0-url-csp.md`](protocol/tier-definition/version-0-url-csp.md)

### Session 0x02 — host response: version 2 compact tier definitions (VGS)
→ [`protocol/tier-definition/version-2-compact-vgs.md`](protocol/tier-definition/version-2-compact-vgs.md)

### Tag 0x03 — config parameter
→ [`protocol/tier-definition/tag-03-config-param.md`](protocol/tier-definition/tag-03-config-param.md)

### Chunking (both versions, both directions)
→ [`protocol/tier-definition/chunking.md`](protocol/tier-definition/chunking.md)

## SerialStream session protocol (group 0x43, cmd `7c:00` / `fc:00`)
→ [`protocol/sessions/README.md`](protocol/sessions/README.md)

### Chunk format
→ [`protocol/sessions/chunk-format.md`](protocol/sessions/chunk-format.md)

### CRC algorithm
→ [`protocol/sessions/chunk-format.md`](protocol/sessions/chunk-format.md)

### Acknowledgments
→ [`protocol/sessions/chunk-format.md`](protocol/sessions/chunk-format.md)

### Session open frames
→ [`protocol/sessions/lifecycle.md`](protocol/sessions/lifecycle.md)

### Session close frame
→ [`protocol/sessions/lifecycle.md`](protocol/sessions/lifecycle.md)

### Port / session-byte allocation
→ [`protocol/sessions/lifecycle.md`](protocol/sessions/lifecycle.md)

### Concurrent session map
→ [`protocol/sessions/lifecycle.md`](protocol/sessions/lifecycle.md)

### Compressed transfer format (sessions 0x09, 0x0a)
→ [`protocol/sessions/compressed-0x09-0x0a.md`](protocol/sessions/compressed-0x09-0x0a.md)

### Session 0x03 tile-server envelope (variant, 12 bytes)
→ [`protocol/sessions/session-0x03-tile-envelope.md`](protocol/sessions/session-0x03-tile-envelope.md)

### Type 0x81 — session channel open payload
→ [`protocol/sessions/type-0x81-channel-open.md`](protocol/sessions/type-0x81-channel-open.md)

### Session 0x0a RPC (host → device)
→ [`protocol/sessions/session-0x0a-rpc.md`](protocol/sessions/session-0x0a-rpc.md)

## Dashboard upload protocol
→ [`protocol/dashboard-upload/README.md`](protocol/dashboard-upload/README.md)

### Upload paths — firmware version matrix
→ [`protocol/dashboard-upload/README.md`](protocol/dashboard-upload/README.md)

### Path A — session 0x01 host-initiated FF-prefix upload (plugin implementation)
→ [`protocol/dashboard-upload/path-a-session-01-ff.md`](protocol/dashboard-upload/path-a-session-01-ff.md)

### Path B — session 0x04 device-initiated sub-msg 1/2 (observed in `dash-upload.ndjson`)
→ [`protocol/dashboard-upload/path-b-session-04.md`](protocol/dashboard-upload/path-b-session-04.md)

### Session 0x04 device → host root directory listing (2025-11 firmware)
→ [`protocol/dashboard-upload/session-04-root-dir.md`](protocol/dashboard-upload/session-04-root-dir.md)

### Dashboard config RPC (session 0x09, compressed transfer)
→ [`protocol/dashboard-upload/config-rpc-session-09.md`](protocol/dashboard-upload/config-rpc-session-09.md)

### Session 0x01 management RPC envelope
→ [`protocol/dashboard-upload/session-01-mgmt-rpc.md`](protocol/dashboard-upload/session-01-mgmt-rpc.md)

## Channel configuration burst (group 0x40, post-upload or on connect)
→ [`protocol/channel-config/group-0x40-burst.md`](protocol/channel-config/group-0x40-burst.md)

### 28:00/28:01/28:02 details
→ [`protocol/channel-config/group-0x40-burst.md`](protocol/channel-config/group-0x40-burst.md)

### Post-upload / active display cycle (group 0x43)
→ [`protocol/channel-config/group-0x43-active-display-cycle.md`](protocol/channel-config/group-0x43-active-display-cycle.md)

## LED color commands
→ [`protocol/leds/color-commands.md`](protocol/leds/color-commands.md)

## Other periodic commands
→ [`protocol/periodic/`](protocol/periodic/)

### Group 0x0E parameter table reader / debug console (host → devices 0x12/0x13/0x17, ~9 Hz)
→ [`protocol/periodic/group-0x0E-param-reader.md`](protocol/periodic/group-0x0E-param-reader.md)

### Group 0x1F (host → device 0x12, ~3 Hz)
→ [`protocol/periodic/group-0x1F.md`](protocol/periodic/group-0x1F.md)

### Group 0x28 (host → device 0x13, occasional)
→ [`protocol/periodic/group-0x28.md`](protocol/periodic/group-0x28.md)

### Group 0x29 (host → device 0x13, once during config)
→ [`protocol/periodic/group-0x29.md`](protocol/periodic/group-0x29.md)

### Group 0x2B (host → device 0x13, occasional)
→ [`protocol/periodic/group-0x2B.md`](protocol/periodic/group-0x2B.md)

## Complete telemetry startup timeline
→ [`protocol/startup-timeline.md`](protocol/startup-timeline.md)

### Concurrent outbound streams during active telemetry
→ [`protocol/startup-timeline.md`](protocol/startup-timeline.md)

### Preamble detail — from `moza-startup.json` (2026-04-12, raw Wireshark JSON)
→ [`protocol/startup-timeline.md`](protocol/startup-timeline.md)

### Full connect-to-telemetry — from `connect-wheel-start-game.json`
→ [`protocol/startup-timeline.md`](protocol/startup-timeline.md)

## Plugin implementation
→ [`protocol/plugin/`](protocol/plugin/)

### Startup phases
→ [`protocol/plugin/startup-phases.md`](protocol/plugin/startup-phases.md)

### Session management
→ [`protocol/plugin/session-management.md`](protocol/plugin/session-management.md)

### Tier definition implementation
→ [`protocol/plugin/tier-impl.md`](protocol/plugin/tier-impl.md)

### Reassembly fallback
→ [`protocol/plugin/reassembly-fallback.md`](protocol/plugin/reassembly-fallback.md)

## Setting value encoding
→ [`protocol/settings/`](protocol/settings/)

### Wheel settings (group 0x3F/0x40, device 0x17)
→ [`protocol/settings/wheel-0x17.md`](protocol/settings/wheel-0x17.md)

### Dashboard settings (group 0x32/0x33, device 0x14)
→ [`protocol/settings/dashboard-0x14.md`](protocol/settings/dashboard-0x14.md)

## EEPROM direct access (group 0x0A / 10)
→ [`protocol/settings/eeprom-0x0A.md`](protocol/settings/eeprom-0x0A.md)

## Base ambient LED control (groups 0x20/0x22 — 32/34)
→ [`protocol/leds/base-ambient-0x20-0x22.md`](protocol/leds/base-ambient-0x20-0x22.md)

## Wheel LED group architecture (groups 0x3F/0x40 — 63/64, extended)
→ [`protocol/leds/wheel-groups-0x3F-0x40.md`](protocol/leds/wheel-groups-0x3F-0x40.md)

## Internal bus topology (monitor.json)
→ [`protocol/transport/internal-bus.md`](protocol/transport/internal-bus.md)

## SimHub plugin vs PitHouse wire divergence (2026-04-21)
→ [`protocol/findings/2026-04-21-simhub-vs-pithouse-divergence.md`](protocol/findings/2026-04-21-simhub-vs-pithouse-divergence.md)

### Functional split (expected divergence)
→ [`protocol/findings/2026-04-21-simhub-vs-pithouse-divergence.md`](protocol/findings/2026-04-21-simhub-vs-pithouse-divergence.md)

### Probe frame differences
→ [`protocol/findings/2026-04-21-simhub-vs-pithouse-divergence.md`](protocol/findings/2026-04-21-simhub-vs-pithouse-divergence.md)

### Periodic polling not done by PitHouse
→ [`protocol/findings/2026-04-21-simhub-vs-pithouse-divergence.md`](protocol/findings/2026-04-21-simhub-vs-pithouse-divergence.md)

### One-shot writes unique to SimHub
→ [`protocol/findings/2026-04-21-simhub-vs-pithouse-divergence.md`](protocol/findings/2026-04-21-simhub-vs-pithouse-divergence.md)

### Session counts
→ [`protocol/findings/2026-04-21-simhub-vs-pithouse-divergence.md`](protocol/findings/2026-04-21-simhub-vs-pithouse-divergence.md)

### What SimHub is missing vs PitHouse
→ [`protocol/findings/2026-04-21-simhub-vs-pithouse-divergence.md`](protocol/findings/2026-04-21-simhub-vs-pithouse-divergence.md)

### Items likely worth fixing in SimHub
→ [`protocol/findings/2026-04-21-simhub-vs-pithouse-divergence.md`](protocol/findings/2026-04-21-simhub-vs-pithouse-divergence.md)

### Phase 3 — dashboard/config upload blobs (NOT implemented in SimHub)
→ [`protocol/findings/2026-04-21-simhub-vs-pithouse-divergence.md`](protocol/findings/2026-04-21-simhub-vs-pithouse-divergence.md)

### Phase 4 — fw_debug subscription (NOT implemented, intentional)
→ [`protocol/findings/2026-04-21-simhub-vs-pithouse-divergence.md`](protocol/findings/2026-04-21-simhub-vs-pithouse-divergence.md)

### Session 0x09 configJson burst timing (real wheel vs sim)
→ [`protocol/findings/2026-04-21-simhub-vs-pithouse-divergence.md`](protocol/findings/2026-04-21-simhub-vs-pithouse-divergence.md)

## PitHouse-observed deviations (2026-04-21 sim captures)
→ [`protocol/findings/2026-04-21-pithouse-deviations.md`](protocol/findings/2026-04-21-pithouse-deviations.md)

### configJsonList is NOT factory-canonical
→ [`protocol/findings/2026-04-21-pithouse-deviations.md`](protocol/findings/2026-04-21-pithouse-deviations.md)

### Session 0x0a RPC id is target-scoped, not counter
→ [`protocol/findings/2026-04-21-pithouse-deviations.md`](protocol/findings/2026-04-21-pithouse-deviations.md)

### `completelyRemove` arg does not match sim-advertised ids (2026-04-22)
→ [`protocol/findings/2026-04-21-pithouse-deviations.md`](protocol/findings/2026-04-21-pithouse-deviations.md)

### PitHouse cache-skip prevents upload RPC under some condition we can't bypass from sim
→ [`protocol/findings/2026-04-21-pithouse-deviations.md`](protocol/findings/2026-04-21-pithouse-deviations.md)

### Display sub-device identity randomisation required, not just wheel identity
→ [`protocol/findings/2026-04-21-pithouse-deviations.md`](protocol/findings/2026-04-21-pithouse-deviations.md)

### Session 0x04 root dir listing: persistent paths expected
→ [`protocol/findings/2026-04-21-pithouse-deviations.md`](protocol/findings/2026-04-21-pithouse-deviations.md)

### Pithouse does not re-push dictionary blobs on reconnect
→ [`protocol/findings/2026-04-21-pithouse-deviations.md`](protocol/findings/2026-04-21-pithouse-deviations.md)

### Canonical RPC method envelope variants
→ [`protocol/findings/2026-04-21-pithouse-deviations.md`](protocol/findings/2026-04-21-pithouse-deviations.md)

### Session 0x03 is host→wheel ONLY (verified 2026-04-22)
→ [`protocol/findings/2026-04-21-pithouse-deviations.md`](protocol/findings/2026-04-21-pithouse-deviations.md)

### Dashboard upload traffic missing when PitHouse thinks wheel has dashboard
→ [`protocol/findings/2026-04-21-pithouse-deviations.md`](protocol/findings/2026-04-21-pithouse-deviations.md)

### Cold-start: PitHouse skips tier_def push on reconnect (2026-04-24)
→ [`protocol/findings/2026-04-21-pithouse-deviations.md`](protocol/findings/2026-04-21-pithouse-deviations.md)

### configJson state push includes top-level fields many docs omit
→ [`protocol/findings/2026-04-21-pithouse-deviations.md`](protocol/findings/2026-04-21-pithouse-deviations.md)

## Findings from 2026-04-24 deep-dive (CSP on R9)
→ [`protocol/findings/2026-04-24-csp-deep-dive.md`](protocol/findings/2026-04-24-csp-deep-dive.md)

### Short-form identity probes (grp 0x43, dev 0x17) — no sub-byte variant
→ [`protocol/findings/2026-04-24-csp-deep-dive.md`](protocol/findings/2026-04-24-csp-deep-dive.md)

### Hub (dev 0x12) and base (dev 0x13) identity cascade
→ [`protocol/findings/2026-04-24-csp-deep-dive.md`](protocol/findings/2026-04-24-csp-deep-dive.md)

### Session cumulative ACKs — `fc:00 [sess] [seq_lo] [seq_hi]` (5-byte payload)
→ [`protocol/findings/2026-04-24-csp-deep-dive.md`](protocol/findings/2026-04-24-csp-deep-dive.md)

### Session data chunk CRC format
→ [`protocol/findings/2026-04-24-csp-deep-dive.md`](protocol/findings/2026-04-24-csp-deep-dive.md)

### Session 1 device→host content differs from sim's emit
→ [`protocol/findings/2026-04-24-csp-deep-dive.md`](protocol/findings/2026-04-24-csp-deep-dive.md)

### CSP session 2 desc chunk sizes: 24/5/2/9/2 (42B), not 26/5/2/9/2 (44B)
→ [`protocol/findings/2026-04-24-csp-deep-dive.md`](protocol/findings/2026-04-24-csp-deep-dive.md)

### `43:17:fc:00:*` probe family is host-side ACK, NOT a real probe
→ [`protocol/findings/2026-04-24-csp-deep-dive.md`](protocol/findings/2026-04-24-csp-deep-dive.md)

### Hub/base identity probes are 0x00-prefixed, not empty-payload
→ [`protocol/findings/2026-04-24-csp-deep-dive.md`](protocol/findings/2026-04-24-csp-deep-dive.md)

### dev_type table (per-wheel, all 4 bytes)
→ [`protocol/identity/dev-type-table.md`](protocol/identity/dev-type-table.md)

### hw_id must match between session1_desc and cmd 0x06
→ [`protocol/findings/2026-04-24-csp-deep-dive.md`](protocol/findings/2026-04-24-csp-deep-dive.md)

### enableManager.dashboards — factory-populated on empty FS
→ [`protocol/findings/2026-04-24-csp-deep-dive.md`](protocol/findings/2026-04-24-csp-deep-dive.md)

### Dev 0x19 is pedal (newer firmware)
→ [`protocol/identity/pedal-0x19.md`](protocol/identity/pedal-0x19.md)

### Session 0x06 file-transfer paths (2026-04 firmware only)
→ [`protocol/findings/2026-04-24-csp-deep-dive.md`](protocol/findings/2026-04-24-csp-deep-dive.md)

### Channel catalog TLV framing
→ [`protocol/findings/2026-04-24-csp-deep-dive.md`](protocol/findings/2026-04-24-csp-deep-dive.md)

## Findings from 2026-04-24 session (firmware upload path on new builds)
→ [`protocol/findings/2026-04-24-firmware-upload-path.md`](protocol/findings/2026-04-24-firmware-upload-path.md)

### File upload session is NOT 0x06 — varies per firmware
→ [`protocol/findings/2026-04-24-firmware-upload-path.md`](protocol/findings/2026-04-24-firmware-upload-path.md)

### Device-side `7c:23` page-activate frames vary per wheel
→ [`protocol/findings/2026-04-24-firmware-upload-path.md`](protocol/findings/2026-04-24-firmware-upload-path.md)

### `7c:23` is a device-initiated session-open request, not just a page notification
→ [`protocol/findings/2026-04-24-firmware-upload-path.md`](protocol/findings/2026-04-24-firmware-upload-path.md)

### Session 0x04 directory-listing probe/reply format
→ [`protocol/findings/2026-04-24-firmware-upload-path.md`](protocol/findings/2026-04-24-firmware-upload-path.md)

### Dir-listing reply 176B tail — NOT decodable as zlib
→ [`protocol/findings/2026-04-24-firmware-upload-path.md`](protocol/findings/2026-04-24-firmware-upload-path.md)

### File-transfer sub-message format — LOCAL marker varies
→ [`protocol/findings/2026-04-24-firmware-upload-path.md`](protocol/findings/2026-04-24-firmware-upload-path.md)

### Upload protocol handshake sequence
→ [`protocol/dashboard-upload/upload-handshake-2026-04.md`](protocol/dashboard-upload/upload-handshake-2026-04.md)

### 1-byte XOR status after `ff*4` sentinel (not a 4-byte trailer)
→ [`protocol/dashboard-upload/upload-handshake-2026-04.md`](protocol/dashboard-upload/upload-handshake-2026-04.md)

### Session data chunk CRC — 4 bytes LE
→ [`protocol/sessions/chunk-format.md`](protocol/sessions/chunk-format.md)

### Multi-round upload content (type=0x03) — zlib reassembly
→ [`protocol/dashboard-upload/upload-handshake-2026-04.md`](protocol/dashboard-upload/upload-handshake-2026-04.md)

### configJson state `rootDirPath` changed between firmware versions
→ [`protocol/dashboard-upload/config-rpc-session-09.md`](protocol/dashboard-upload/config-rpc-session-09.md)

### 2026-04 PitHouse omits the remote dashboard path
→ [`protocol/dashboard-upload/path-b-session-04.md`](protocol/dashboard-upload/path-b-session-04.md)

### 6-byte sub-msg header (new firmware, 2026-04+)
→ [`protocol/dashboard-upload/6-byte-submsg-header.md`](protocol/dashboard-upload/6-byte-submsg-header.md)

### Per-chunk metadata trailer (continuation chunks)
→ [`protocol/dashboard-upload/per-chunk-trailer.md`](protocol/dashboard-upload/per-chunk-trailer.md)

### Multi-attempt interleaving in the buffer
→ [`protocol/dashboard-upload/multi-attempt-interleaving.md`](protocol/dashboard-upload/multi-attempt-interleaving.md)

### empty `enableManager.dashboards` no longer blocks handshake
→ [`protocol/findings/2026-04-24-firmware-upload-path.md`](protocol/findings/2026-04-24-firmware-upload-path.md)

### Pedal device 0x19 identity (KS Pro capture)
→ [`protocol/identity/pedal-0x19.md`](protocol/identity/pedal-0x19.md)

### Hub (dev 0x12) and base (dev 0x13) identity are byte-identical
→ [`protocol/identity/hub-base-cascade.md`](protocol/identity/hub-base-cascade.md)

## AB9 active shifter (2026-04-24)
→ [`protocol/devices/ab9-shifter.md`](protocol/devices/ab9-shifter.md)

### USB enumeration
→ [`protocol/devices/ab9-shifter.md`](protocol/devices/ab9-shifter.md)

### Shifter mode set — `Group 0x1F → dev 0x12, cmd 0xD300`
→ [`protocol/devices/ab9-shifter.md`](protocol/devices/ab9-shifter.md)

### Stored-on-device settings — `Group 0x1F → dev 0x12`
→ [`protocol/devices/ab9-shifter.md`](protocol/devices/ab9-shifter.md)

### Sliders that produced **no** USB write
→ [`protocol/devices/ab9-shifter.md`](protocol/devices/ab9-shifter.md)

### Shift-trigger feedback is firmware-driven; engine vibration needs telemetry
→ [`protocol/devices/ab9-shifter.md`](protocol/devices/ab9-shifter.md)

## Open questions
→ [`protocol/open-questions.md`](protocol/open-questions.md)
