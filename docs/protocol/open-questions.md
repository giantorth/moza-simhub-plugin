## Open questions

- **Dashboard upload path ambiguity** — Two upload structures documented in [`dashboard-upload/`](dashboard-upload/) (Path A session 0x01 FF-prefix vs Path B session 0x04 sub-msg 1/2). Not yet confirmed whether these are:
  - (a) Two different upload paths firmware supports in parallel
  - (b) Same path described from different capture/understanding eras (one is stale)
  - (c) Different firmware versions (2026-04 vs 2025-11)

  Plugin currently implements Path B. Needs side-by-side capture on both firmware versions to resolve whether Path A is still needed on older firmware.

- **Dashboard byte limit configuration** — stored at config object offset `+0x30`, set during dashboard upload (group 0x40). Exact mechanism for setting this limit not yet traced.

- **Cold-start initialization** — EEPROM persistence across power cycles confirmed for channel config; unclear for session state.

- **MDD (standalone dash)** — no captures of telemetry sent to device 0x14; protocol may differ.

- **Dashboard upload: per-field pacing** — Plugin sends all upload chunks (across all 3 FF-prefixed fields) in a single burst, then waits for ack. PitHouse may instead pace by field: send field 0 chunks → wait for ack → send field 1 → wait → send field 2. Burst approach matches how tier definitions are sent (also tight-loop, working). If large dashboards fail while small ones succeed, try adding per-field ack waits.

- **Dashboard upload: seq=2 assumes port=1** — Data chunks on mgmt session start at seq=2, assuming session open used seq=1 (i.e. mgmtPort=1). Session open frame uses seq=port, so data should start at port+1. Since serial port is exclusive (PitHouse cannot run simultaneously), port probing always finds ports 1 and 2, making seq=2 correct in practice. Same assumption in tier definition code (seq=3, assumes telemetry port=2). If this changes (e.g. multi-client over network), both need to use `port + 1` instead of hardcoded values.

- **EEPROM direct access** — group 10 protocol found in rs21_parameter.db but never observed in USB captures; needs live verification.

- **Base ambient LEDs** — groups 32/34 commands found in rs21_parameter.db; not captured in USB traces (requires base with LED strips).

- **Wheel LED groups 2-4** — Single (28), Rotary (56), Ambient (12) groups found in rs21_parameter.db; only groups 0 (Shift/RPM) and 1 (Button) confirmed in captures so far. **Partial plugin support (2026-04-19)**: commands added for `1F [G] FF [N]` per-LED color, `1B [G] FF` brightness, `1C [G]` mode; experimental Wheel Settings panel exposes per-slot Range (min/max) + Fill/Clear/Send-one/Brightness/Mode controls for groups 0-4 plus Meter flag LEDs (slot 5). Brightness-read probe lights groups 2/3/4 panels when firmware answers — **probe unreliable** (firmware acknowledges reads for parameters with no physical hardware; confirmed on base KS wheel which has no rotary/ambient hardware but responds to all three probes). Use panel's summary TextBox to record per-wheel support and feed back into `WheelModelInfo`. No live telemetry equivalent (`25 G` / `26 G` bulk+bitmask) found for groups 2-4 — diagnostic uses static per-LED writes only.

- **Group 0x09 semantics** — presence/ready check sent first during probe. Response `00 01` may indicate sub-device count (VGS has 1 Display sub-device). Needs verification with other wheel models.

- **Group 0x28 / 0x29 purpose** — group 0x28 queries base for per-device parameters (values 450, 1000 seen); group 0x29 sets base parameter (value 1100). Possibly FFB or calibration related.
