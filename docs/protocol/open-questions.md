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

- **Session-0x01 `ff` push: `kind` field semantics** — host→wheel property pushes (see [`findings/2026-04-29-session-01-property-push.md`](findings/2026-04-29-session-01-property-push.md)) carry a `kind:u32` field whose meaning is unclear: brightness=0/25/41/50 use `kind=1` (transient slider) while brightness=100 baseline uses `kind=14` (persisted re-emit), and standby uses `kind=10` (u64 ms duration). Open whether `kind` is a property-id table, a value-encoding selector, or both. Capture other property pushes (RPM colors, flag colors, indicator modes) to disambiguate. Also: are there pushes with `size > 12` (strings, colour triplets, multi-field structs)?

- **Dashboard switch wire signal — candidates identified, UNTESTED** (2026-04-30). Two distinct host→wheel signals seen during user-driven switches in `wireshark/csp/startup, change knob colors, change dash several times, delete dash.pcapng`:
  1. **FF-record on session 0x02** (primary candidate): 25-byte payload `ff 0c 00 00 00 [data_crc:LE32] [field1=4:LE32] [slot:LE32] [0:LE32] [body_crc:LE32]`. Slot = 1-based index into wheel's enabled-dashboards list (session 0x09 `enableManager.dashboards`). Sourced from external RE writeup; matches records observed in our capture at t=212–238s (slots 0,1,0,2,3,4,10).
  2. **`0x3F 0x17 27:[page]` direct write** (secondary / role TBD): per-page 4-byte fingerprint update at t=150.57 / 166.78s. Fingerprint is wheel-assigned opaque value, not a hash of visible fields.

  Both paths verified from capture but UNTESTED from plugin side. See [`findings/2026-04-30-dashboard-switch-3f27.md`](findings/2026-04-30-dashboard-switch-3f27.md) for full wire formats and implementation notes. Recommend trying FF-record path first; if wheel ignores it, investigate `3F 27:NN` with a harvested fingerprint table.

- ~~**Tier-flag → package_level mapping inverted between plugin paths**~~ — RESOLVED 2026-04-29. Type02 firmware uses **flag=2 = fastest tier**, NOT flag=0. Wheel binds widgets to the highest flag value. Plugin's `TelemetrySender.Profile` setter now expands single-pkg_level profiles to 3 tiers and assigns the fastest pkg_level to tier 2 (flag=2). Confirmed live: Nebula widgets render value updates only when fast-tick frames carry flag=2.

- ~~**Telemetry-flag count vs package_level count**~~ — RESOLVED 2026-04-30. PitHouse multi-pkg-level dashboards emit `4 broadcasts × N sub-tiers per broadcast`, where each broadcast's sub-tiers cover all `package_level` rates; flag bytes increment monotonically across all sub-tiers. Single-pkg-level dashboards use 3 broadcasts × 1 sub-tier. Plugin formula: `broadcasts = (subCount == 1) ? 3 : max(4, subCount + 1)`. See [`tier-definition/version-2-compact-vgs.md`](tier-definition/version-2-compact-vgs.md) for the full pattern.

- **PitHouse per-dashboard sub-tier split source** — PitHouse's Grids tier-def splits 8 channels into 5+2+1 across `pkg_level` 30/500/2000 sub-tiers, but `Data/Telemetry.json` marks all 8 as pkg=30 only. PitHouse must source the split from somewhere outside `Telemetry.json` (likely embedded in PitHouse's own dashboard catalogs). Plugin currently uses `Telemetry.json` pkg_level grouping (8+12 split for Grids); wheel still renders successfully because tier-def is internally consistent and widget binding goes through channel idx, not flag/sub-tier position. Open whether PitHouse's split is just metadata or actually changes wheel-side decoding behaviour.

- **Type02 inferred compression codes** — `0x10` (`tyre_pressure_1`), `0x11` (`tyre_temp_1`), `0x12` (`track_temp_1`), `0x13` (`oil_pressure_1`), `0x15` (`float_600_2`), `0x16` (`brake_temp_1`) appear in plugin's `TierDefinitionBuilder.CompressionCodes` table marked as "inferred". Live R5+W17 capture 2026-04-30 confirms `0x10`/`0x11` are NOT decoded — tyre widgets stay at 0 until plugin switches the URL to `float` (`0x07`, width 32). Other inferred codes still untested; assume broken on Type02 until proven otherwise. May be valid on older firmware (VGS/CS/KS) but no live capture on those wheels uses tyres.

- **Plugin Telemetry.json sparse SimHub mappings** — As of 2026-04-30, `Data/Telemetry.json` contains 454 channel sectors but only ~17 have non-empty `simhub_property` / `simhub_field`. ABS/TC + 8 tyre channels mapped 2026-04-30; ~437 sectors still rely on the heuristic fallback in `DashboardProfileStore.PickCompressionForUrl` for compression and have no live SimHub data binding. Backfilling these is mechanical (most match `DataCorePlugin.GameData.<URL_suffix>` 1:1); needs a sweep + verification per dashboard.

- ~~**Plugin V0/Type02/V2 dispatch**~~ — RESOLVED 2026-04-29. Type02 firmware uses the **V2 bit-packed `7d:23` path with LEGACY N convention (N = 8+data, NOT 10+data)**. Plugin's `useV0Values = ProtocolVersion == 0` check at `TelemetrySender.cs:2257` routes to V2; `type02NConvention=false` is hard-set in the Profile setter so frames have N=14 instead of N=16. Verified byte-identical to PitHouse 2026-04-29 nebula capture, wheel renders correctly. Earlier guess that Type02 needs V0 chunked path was wrong.
