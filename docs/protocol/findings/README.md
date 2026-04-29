# Findings — dated deep-dive journal

Verbatim journal entries from capture-analysis sessions. Kept dated for traceability of when each fact was learned. Canonical info has been migrated into the topical pages where stable; this folder retains the original deep-dive context.

| File | Topic |
|------|-------|
| [`2026-04-21-simhub-vs-pithouse-divergence.md`](2026-04-21-simhub-vs-pithouse-divergence.md) | Side-by-side SimHub vs PitHouse wire capture: probe diffs, periodic polling, session counts, missing phases |
| [`2026-04-21-pithouse-deviations.md`](2026-04-21-pithouse-deviations.md) | PitHouse behaviour observed during sim testing that deviates from / refines docs elsewhere |
| [`2026-04-24-csp-deep-dive.md`](2026-04-24-csp-deep-dive.md) | CSP wheel on R9 base (`346e:0002`, 2026-04 firmware): identity probes, ACK format, chunk CRC, session 1 desc, hw_id rules |
| [`2026-04-24-firmware-upload-path.md`](2026-04-24-firmware-upload-path.md) | New-firmware (2026-04+) upload path findings: file-transfer session varies, `7c:23` semantics, dir-listing format, sub-msg LOCAL marker, multi-attempt interleaving |
| [`2026-04-28-wheel-catalog-read.md`](2026-04-28-wheel-catalog-read.md) | First-time wheel-detect catalog sweep: 83 unique `0x40 0x17` reads in ~750 ms, full LED-state snapshot (28 `1F` reads), `29 00` undocumented |
