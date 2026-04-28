# Moza protocol reference

Hierarchical split of the original `docs/moza-protocol.md`. Layout is **function-first, device-second** — most protocol detail is cross-cutting (telemetry frame format applies to all wheels), so functional folders are the primary axis. Per-device files in [`devices/`](devices/) hold device-scoped command tables (groups, sub-cmds, byte widths) that don't fit cleanly into the cross-cutting topic folders.

> **Status (2026-04-28):** Hierarchical split complete; leaf pages expanded with frame layouts, field tables, byte offsets, and worked examples (2026-04-28 pass). Some sections that were originally dated "findings" entries have been split out into their topical homes (e.g. `dev_type` table → [`identity/dev-type-table.md`](identity/dev-type-table.md)); see `docs/moza-protocol.md` for the full redirect map.

## Layout

| Folder | Scope |
|--------|-------|
| [`wire/`](wire/) | Frame header, checksum, 0x7E byte stuffing, response transforms, wheel write echoes, command chaining |
| [`transport/`](transport/) | USB interfaces and endpoints, internal serial bus topology |
| [`identity/`](identity/) | Device identity probes, sub-device wrapping, model name table, dev-type table, per-device identity quirks |
| [`telemetry/`](telemetry/) | Channel catalog, value encoding, live telemetry stream (`0x43/0x17 7D 23`), enable/disable control |
| [`sessions/`](sessions/) | SerialStream session layer (`7c:00`/`fc:00`): chunk format, CRC, ACKs, lifecycle, port allocation, compressed transfers |
| [`tier-definition/`](tier-definition/) | Session 0x01/0x02 handshake, device description, channel catalog response variants (CSP v0 / VGS v2), config parameters |
| [`dashboard-upload/`](dashboard-upload/) | Dashboard upload paths (A: session 0x01 FF-prefix, B: session 0x04 sub-msg), config RPC, mgmt RPC, sub-msg headers, chunk trailers |
| [`channel-config/`](channel-config/) | Group 0x40 burst, post-upload / active display cycle |
| [`leds/`](leds/) | LED color commands, base ambient strips (`0x20/0x22`), wheel LED group architecture (`0x3F/0x40` extended) |
| [`settings/`](settings/) | Wheel settings (`0x3F/0x40`, dev `0x17`), dashboard settings (`0x32/0x33`, dev `0x14`), EEPROM direct access (`0x0A`) |
| [`periodic/`](periodic/) | Group `0x0E` parameter reader, `0x1F`, `0x28`, `0x29`, `0x2B` periodic / occasional commands |
| [`devices/`](devices/) | Per-device pages — main hub (`0x12`), wheelbase (`0x13`), dash (`0x14`), wheel (`0x17`), pedals (`0x19`), shifter / handbrake / e-stop, AB9 active shifter. Device ID table cross-links into functional pages |
| [`plugin/`](plugin/) | SimHub plugin implementation notes: startup phases, session management, tier impl, reassembly fallback |
| [`findings/`](findings/) | Dated journal entries from deep-dive sessions. Kept verbatim for traceability; canonical info is reflected in the topical pages |

## Top-level pages

- [`heartbeat.md`](heartbeat.md) — heartbeats, keepalives, unsolicited messages
- [`startup-timeline.md`](startup-timeline.md) — full connect-to-telemetry sequence
- [`open-questions.md`](open-questions.md) — outstanding unknowns

## Reference pages

- [`GLOSSARY.md`](GLOSSARY.md) — jargon, wheel/base model names, firmware eras, protocol-layer terms
- [`FIRMWARE.md`](FIRMWARE.md) — firmware-era matrix: which captures, which wheels, which pages are era-specific
- [`../../usb-capture/CAPTURES.md`](../../usb-capture/CAPTURES.md) — per-capture inventory (wheel, software, scenario, observed traffic)

## Cross-cutting references

- Foundational frame format applies to **all** device traffic. Read [`wire/`](wire/) before anything else.
- Authoritative command DB: `Pit House/bin/rs21_parameter.db` (SQLite, 919 commands). Per-device command tables in [`devices/`](devices/); value-encoding rules in [`telemetry/service-parameter-transforms.md`](telemetry/service-parameter-transforms.md).
- USB capture methodology: see `docs/usb-capture.md`.
- Plugin-side wire divergence and PitHouse-observed deviations: see [`findings/`](findings/).
