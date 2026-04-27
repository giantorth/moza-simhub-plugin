### dev_type table (per-wheel, all 4 bytes)

`0x04 response` payload `01 02 [DT_2] [DT_3]`. Earlier docs assumed only DT_2 varies and DT_3 was always `0x06`; KS Pro re-extraction 2026-04-26 showed DT_3 also varies, so the table now lists both:

| Wheel | DT_2 DT_3 | Source |
|-------|-----------|--------|
| VGS | `04 06` | capture, old |
| CSP | `06 06` (DT_2 not `0x04`!) | 2026-04 capture — sim default `01:02:04:06` was wrong for CSP |
| KS | `05 06` | live probe |
| KSP | `07 07` | 2026-04-26 re-extract from `usb-capture/ksp/putOnWheelAndOpenPitHouse.pcapng` grp=0x84 reply. Earlier sim default `01:02:05:06` (copied from KS) made PitHouse name-match W18 → KS Pro briefly, then demote on dev_type read because `05 06` matches the non-Pro KS profile |
| ES | `12 08` (base-proxied) | live probe |

Display sub-device `0x04 response` payload `01 02 [DDT] [06]`:

| Wheel | DDT byte | Source |
|-------|----------|--------|
| CSP | 0x11 (not 0x0d!) — fixed in profile 2026-04-24 |
| KSP | 0x11 — same as CSP (shares W17-HW RGB display module). Earlier sim profile had `0x0d` which the plugin_probe table won via longest-prefix match over the correct `0x11` from `kspro_wheel_17.json` replay |
| VGS | 0x08 |
