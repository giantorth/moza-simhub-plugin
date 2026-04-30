# Findings — dated deep-dive journal

Verbatim journal entries from capture-analysis sessions. Kept dated for traceability of when each fact was learned. Canonical info has been migrated into the topical pages where stable; this folder retains the original deep-dive context.

| File | Topic |
|------|-------|
| [`2026-04-21-simhub-vs-pithouse-divergence.md`](2026-04-21-simhub-vs-pithouse-divergence.md) | Side-by-side SimHub vs PitHouse wire capture: probe diffs, periodic polling, session counts, missing phases |
| [`2026-04-21-pithouse-deviations.md`](2026-04-21-pithouse-deviations.md) | PitHouse behaviour observed during sim testing that deviates from / refines docs elsewhere |
| [`2026-04-24-csp-deep-dive.md`](2026-04-24-csp-deep-dive.md) | CSP wheel on R9 base (`346e:0002`, 2026-04 firmware): identity probes, ACK format, chunk CRC, session 1 desc, hw_id rules |
| [`2026-04-24-firmware-upload-path.md`](2026-04-24-firmware-upload-path.md) | New-firmware (2026-04+) upload path findings: file-transfer session varies, `7c:23` semantics, dir-listing format, sub-msg LOCAL marker, multi-attempt interleaving |
| [`2026-04-28-wheel-catalog-read.md`](2026-04-28-wheel-catalog-read.md) | First-time wheel-detect catalog sweep: 83 unique `0x40 0x17` reads in ~750 ms, full LED-state snapshot (28 `1F` reads), `29 00` undocumented |
| [`2026-04-29-session-01-property-push.md`](2026-04-29-session-01-property-push.md) | PitHouse pushes wheel-integrated dashboard settings (brightness, display-standby) via session-0x01 `ff`-tagged records: `ff <size:u32> <crc32_LE:4> <kind:u32> <value:u32-or-u64>`. Inner CRC = `zlib.crc32(kind ‖ value)`, verified against 7 captures |
| [`2026-04-29-dashboard-initial-sync.md`](2026-04-29-dashboard-initial-sync.md) | Cold-attach burst on dev `0x17` integrated wheel-display: present-ack `80`, wrapped `0xC3/0x71` identity records, parallel `0x82..0x91` direct device broadcasts, two-slot `7c 00` catalog (device-state + telemetry-property list). New `0x84` byte-2 = `0x11` (RGB-variant), preliminary record shape `7c 00 [slot][page][idx][kind:u16][length:u32][data][crc32]` |
| [`2026-04-30-multi-pkg-dashboards.md`](2026-04-30-multi-pkg-dashboards.md) | Multi-pkg-level dashboards rendering on Type02 (Nebula/Rally V4/Grids): broadcast count formula `subCount==1 ? 3 : max(4, subCount+1)`; PitHouse per-dashboard sub-tier split is custom and unrelated to `Telemetry.json` pkg_level; tyre compression codes `0x10`/`0x11` not decoded by Type02 — use `float` (`0x07`); plugin Telemetry.json missing ~437 SimHub mappings; TestMode phase scaling for slow tiers |
