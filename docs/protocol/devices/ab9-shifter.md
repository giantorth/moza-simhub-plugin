## AB9 active shifter (2026-04-24)

**Status (2026-05-15)**: full PitHouse-replicating implementation in `Devices/MozaAb9DeviceManager.cs` — FFB init handshake, multi-stream worker (engine-vib `0x0A 0x05`, pulse pair `0x0B 0x02/03`, triggers `0x0D 0x01/02/03/05`, low-rate signed pair `0x08 0x04/06`), corrected `0x1E` read group, bus-hint detection. Verified on real AB9 hardware: detection latches to "AB9 connected" within ~1 s, engine rumble fires with intensity/frequency sliders driving amplitude and oscillator period, H-pattern + slider config + gear-shift vibration all working.

Captures: `usb-capture/AB9/{Shifter mode change,PitHouse settings change,Launch and H-pattern gear engage,SQ gear change}.pcapng` plus event-time spreadsheet `Moza AB9.xlsx`. Captured on Windows host with PitHouse driving the shifter while wheelbase was also attached. Engine-vibration streams decoded against `sim/logs/ab9-game-20260513.jsonl` (PitHouse + Assetto Corsa, 942,634 frames, 40.9 min) — see `tools/ab9-decode-session` for the analysis pipeline.

### USB enumeration

AB9 enumerates as its **own** Moza composite USB device (VID `0x346E` PID `0x1000`), parallel to the wheelbase (PID `0x0006`). Same 3-interface layout: CDC ACM (EP 0x02 OUT / 0x82 IN, the Moza protocol bus) + HID (EP 0x03 OUT / 0x83 IN). Host writes to the AB9 use the AB9's own CDC pipe — they do **not** route through the wheelbase. HID-OUT (0x03) was never used in any capture; all configuration travels on CDC.

Address-disambiguation (only Moza devs in capture): wheelbase OUTs target dev IDs 0x13/0x14/0x15/0x17/0x19/0x1A/0x1B/0x1E (full sub-bus). AB9 OUTs target only `Main/Hub (0x12)` — confirms AB9 has its own internal "Main" with no sub-devices.

### Shifter mode set — `Group 0x1F → dev 0x12, cmd 0xD300`

Six mode-change events at 5/10/15/20/25/30 s in the shifter-mode capture, each one 8-byte CDC OUT frame on the AB9:

| PitHouse mode | `Config Data` byte |
|---------------|--------------------|
| 5+R Layout 1 | `0x00` |
| 6+R Layout 1 | `0x04` |
| 6+R Layout 2 | `0x05` |
| 7+R Layout 1 | `0x06` |
| 7+R Layout 2 | `0x07` |
| Sequential   | `0x09` |

Gaps `0x01..0x03`, `0x08` are presumably 5+R Layout 2 / other layouts not exercised. Frame shape: `7E 03 1F 12 D3 00 <val> <chk>`.

### Stored-on-device settings — `Group 0x1F → dev 0x12`

PitHouse-settings capture wrote 6 of the 10 sliders as 8-byte `Group 0x1F` frames with single-byte payload (decimal value):

| PitHouse slider                 | Cmd ID  |
|---------------------------------|---------|
| Gear Shift Mechanical Resistance | `0xD600` |
| Spring                           | `0xAF00` |
| Natural Damping                  | `0xB000` |
| Natural Friction                 | `0xB200` |
| Maximum Output Torque Limit      | `0xA900` |

Plus one larger write for **Gear Shift Vibration** (slider event at t=25s/30s in capture): 24-byte CDC frame, `Group 0x20 cmd 0x0A01`, 17-byte payload (the dissector mislabels group 0x20 as "Base Ambient LED Write"). Payload encodes the trigger pattern; high-value byte differs between intensity 100 (`33 2c …`) and intensity 0 (`00 00 …`) at the same payload offset.

### Sliders that produced **no** USB write

Four PitHouse-settings events generated **zero** host→device packets on either USB device:

- **Gear Damping** (events at 15s, 20s)
- **Gear Notchiness** (35s, 40s)
- **Engine vibration intensity** (45s, 50s)
- **Engine vibration frequency** (55s, 60s)

Verified across both EP 0x02 OUT (CDC) and EP 0x03 OUT (HID, never used) on the AB9, and across all OUT pipes on the wheelbase. The slider moves were real (different from/to values per spreadsheet), so PitHouse either: (a) batches these to an "Apply" / save action that wasn't pressed during this capture, (b) renders engine vibration host-side and streams it as continuous output (not configuration), or (c) caches them locally until the next session/connect. Not yet disambiguated — needs a capture with the Apply button pressed, or a SimHub-driven RPM telemetry stream while engine-vib intensity is non-zero.

### Shift-trigger feedback is firmware-driven; engine vibration needs telemetry

The H-pattern (1st→7th + reverse, 18 engage/neutral transitions over 30 s) and SQ gear (6 up/down events incl. holds over 14 s) captures contain **no host→device FFB writes** during shifts. Only sparse identity probes and `Output(d4/d7/d8)` polls — same heartbeat traffic seen at idle. HID IN on EP 0x83 streams continuously at ~1 kHz regardless of shift activity.

For *shift-triggered* effects (notchiness, gear-shift vibration, damping during engage): **firmware-driven**. Host pushes static configuration once, then AB9 firmware detects shifts mechanically, plays back stored vibration pattern + notch/damping per stored settings, and reports new gear via HID IN. Host plays no real-time role in shift feel.

For *engine vibration* (intensity + frequency sliders) and any other RPM- or speed-modulated effect: **resolved as host-rendered streaming on `Group 0x20` — see next section.**

### Engine vibration is host-rendered via `Group 0x20 → dev 0x12` (2026-05-13)

Captured live against an Assetto Corsa session through the `sim/ab9_sim.py` simulator with **PitHouse alone driving the AB9** — no SimHub plugin attached at any point in this session (`sim/logs/ab9-game-20260513.jsonl`, ~36 minutes including idle + redline holds and slider sweeps). PitHouse reads AC telemetry directly and renders the engine-vibration envelope host-side. This resolves option (b) from the previous section: **engine-vib slider movements produce no stored-setting write because PitHouse renders the rumble envelope host-side and streams it continuously as group `0x20` (FFB) param pushes to the AB9's own CDC pipe.** The path is `Group 0x43 / HID-OUT / wheelbase-relay`-free — none of the three speculative routes were used.

Concurrent sub-streams on `Group 0x20 → dev 0x12` during steady-state driving:

| Sub-cmd | Frame len | Rate at idle | Rate at redline | Role |
|---|---|---|---|---|
| `0x0A 0x05` | 19 B | ~85-90 Hz | ~87 Hz | Primary oscillator-period push, one frame per allocated effect slot. See "0x0A 05 payload schema" below — 24-bit BE period field, inversely proportional to vibration frequency (verified 2×). |
| `0x0B 0x02` + `0x0B 0x03` | 22 B each | 1.7 Hz each | 34.6 Hz each | **Engine-cycle pulse train.** `02` carries `… 04 23 28 00 00` (on half), `03` carries `… 04 00 00 00 00` (off half). 16-bit field at payload offset 4-7 advances/varies per pulse (looks like a bipolar envelope sample or per-pulse phase; not yet definitively decoded). **Rate scales linearly with engine speed** — best RPM proxy in the stream. |
| `0x0D 0x02` + `0x0D 0x03` | 3 B (payload `01 D0` / `01 D1`) | 9.1 Hz each | 9.2 Hz each | **Heartbeat-rate trigger** — flat regardless of RPM. Purpose unclear; possibly slot-keepalive. |
| `0x0D 0x05` | 3 B (payload `01 D3`) | 2.0 Hz | 19.1 Hz | RPM-tracking trigger (≈10× scaling from idle to redline). |
| `0x08 0x04` + `0x08 0x06` | 11 B each | <0.1 Hz (1 frame each in 40 s) | not observed at redline | Low-rate update (~3.9 Hz each averaged across whole 190 s session — fired in bursts around state changes, not steady-state). Purpose unknown. |
| `0x0A 0x01`, `0x07 0x01/03/04/09`, `0x0E 0x01/02`, `0x13 0x00` | 2-19 B | 1-2 frames per session each | — | One-shot init / config (the `0x0A 0x01` 24-byte form matches `Gear Shift Vibration` config from the 2026-04-24 capture analysis above; the streaming `0x0A 0x05` form is distinct). |

Effects: PitHouse allocates **6 FFB effect slots** at session start via group `0x20` init (`ffb_init`/`ffb_alloc` in the sim counters); the streams above re-parameterize those slots continuously — no per-frame re-allocation. Slot IDs observed in the session: `1 → 3, 2 → 9, 3 → 9, 4 → 1, 5 → 4, 6 → 1`.

What changes when the user moves the PitHouse "Engine Vibration" slider (50% → 100% verified): the **streamed `0x0A 0x05` values shift to new amplitude/period pairs**. No stored-setting write is observed on `Group 0x1F`. Confirms the slider modulates PitHouse's host-side envelope generator, not a device-resident setting. By extension, the four PitHouse sliders flagged in the previous section as producing zero USB write (`Gear Damping`, `Gear Notchiness`, `Engine vibration intensity`, `Engine vibration frequency`) are very likely **all host-applied modulators** — they only become observable in the wire stream while a sim is running.

#### Engine-vib off → slot ID `0x0000` keepalive

Setting the PitHouse Engine Vibration intensity slider to 0 does **not** silence the `0x0A 0x05` stream. The stream stays at full 91 Hz with the period bytes unchanged from the active state, but the **slot ID flips to `0x0000`** — a silent placeholder that keeps the host→device keepalive alive while signalling "no effective effect". Sim implementation note: handlers should treat slot `0x0000` as a no-op refresh, not an unknown slot.

Sim coverage: the generic group-`0x20` handler in `sim/ab9_sim.py` ACKs every host frame in this session — **zero unhandled frames across 90,187 RX / 90,187 TX** (counters at uptime 458 s). PitHouse + Assetto Corsa drive the simulated AB9 byte-for-byte cleanly.

#### `0x0A 0x05` payload schema (resolved by idle + freq-slider 100→200 Hz test)

```
7E 13 20 12 0A 05 [SS SS] [00 00 00 00 00 00 00] [PP PP PP] 04 [00 00 00 00] [cksum]
                  └ slot   └ 7 zeros              └ 24-bit  └ type tag    └ 4 trailing zeros
                    ID                              period
                    BE                              BE (ticks)
```

- **Slot ID** (2 B BE at offset 6-7): effect/slot identifier — a DirectInput-style FFB effect handle assigned by the host stack when the effective period falls into a particular range. Same slot ID is reused across different `(freq, RPM)` combinations when their `period = K/(rpm × freq)` lands in the same band. Slots observed: `0x1996`, `0x0CCB`, `0x0624`, `0x1478`. The set is *not* a fixed magic constant per parameter — it varies session-to-session (handles are runtime-allocated by Windows).
- **7 zeros** (offset 8-14): purpose unknown; constant across all observations. (Length byte `0x13 = 19 = cmdId(2) + slot(2) + 7 + period(3) + tag(1) + 4` — verified against captured frame `7e1320120a05 1996 00000000000000 4c0301 04 00000000 e2`.)
- **Period** (3 B BE at offset 16-18): oscillator period in device ticks, **inversely proportional to `engine_rpm × freq_slider`**.
- **Type tag** `0x04` (offset 19): constant.
- **4 trailing zeros** (offset 20-23): constant.

Each refresh cycle pushes a *pair* of consecutive frames per slot — typically two close-together period values dithered around a centre (jitter ≈ 0.6% at high freq, up to ~17% at the 50 Hz slider floor).

**Period verification matrix** — all four corners of the (RPM × freq) grid + the slot-ID assignments observed in this session:

| RPM band | Freq slider | Dominant slot(s) | Period (24-bit BE pair) | Avg ticks | Predicted ratio | Measured ratio |
|---|---|---|---|---|---|---|
| idle | 100 Hz | `0x1996` + `0x0CCB` | `0x4702CA / 0x4C0301` | 4.82 M | (reference at 100 Hz) | — |
| idle | 200 Hz | `0x1996` + `0x0624` | `0x250172 / 0x260180` | 2.46 M | 0.5× (freq ×2) | **0.51× = 1.96× shorter** ✓ |
| idle | 50 Hz | `0x1478` | `0xA60682 / 0x8E0594` | 10.1 M | 2.0× vs 100 Hz, 4.0× vs 200 Hz | **2.09× / 4.11×** ✓ |
| redline | 200 Hz | `0x1996` + `0x1478` (harmonics) | `0x050032 / 0x050033` | 327 K | 0.13× (RPM ×7.5) | **0.133×** ✓ |
| redline | 50 Hz | `0x1996` | `0x1400C8 / 0x1400CC` | 1.31 M | 4× vs redline+200Hz, 0.13× vs idle+50Hz | **4.0× / 0.130×** ✓ |

Both axes (freq slider and engine RPM) are independently confirmed: `period = K / (engine_rpm × freq_slider)`, with K ≈ 2.46 M × 200 = 492 M (in `ticks × Hz`) using idle as the RPM=1 reference. At idle RPM ≈ 800 the constant K ≈ 615 K × ticks × Hz / rpm.

Slider effects on the stream:
- **Engine Vibration Frequency**: scales the period inversely; verified across 4× (50 → 200 Hz) at both idle and redline.
- **Engine Vibration Intensity**: controls **slot allocation, not byte values**. Period bytes are byte-identical between intensity levels at the same `(freq, RPM)`; intensity adds/removes simultaneously-streamed slots.

  Slot-count behavior is **RPM-dependent**, not a clean 25%↔1-slot / 100%↔2-slot rule:
  - Idle, freq=200 Hz, int=100% → 2 slots (`0x1996` + `0x0624`)
  - Idle, freq=200 Hz, int=25% → 1 slot (`0x0624` only — `0x1996` drops)
  - Redline, freq=200 Hz, int=25% → 2 slots (`0x0624` + `0x1996`) — both already present at lower intensity than at idle
  - Redline, freq=200 Hz, int=100% → 2 main slots (`0x1996` + `0x1478`) plus transient slot churn during the engine ramp

  At high RPM the host generates richer multi-harmonic effects even at low intensity slider, presumably modelling engine-rumble overtones that scale with engine load.

- **Multi-harmonic layering observed at redline+200 Hz+int=100%**: slot `0x1996` streams two distinct periods in alternation — the 200 Hz fundamental (`0x050032`, ×1731) and a 1/4-frequency overtone (`0x1400C8`, ×617 + `0x1400CC`, ×594) corresponding to 50 Hz. When the freq slider is dropped to 50 Hz the fundamental disappears and only the overtone remains, suggesting the overtone is a fixed engine-model contribution that isn't freq-slider-driven.

#### `0x0A 0x01` payload schema — Gear Shift Vibration intensity (resolved 2026-05-13)

Two `0x0A 0x01` frames observed in the entire session, both at session connect (snapshot push) and at one user-driven slider move:

```
7E 13 20 12 0A 01 [II II] [00 00 00 00 00 00 00 0E 00 64 04 00 00 00 00] [cksum]
                  └ 16-bit BE intensity (range 0 .. 0x332C for 0% .. 100%)
                  └ rest of payload appears static across observations
```

Verification:

| Event | Bytes 0-1 | Decimal | Decoded |
|---|---|---|---|
| Session connect at t=0.11 (PitHouse snapshot push) | `0F 5A` | 3930 | 30.0% |
| User-driven slider move to 100% | `33 2C` | 13100 | 100.0% |

3930 / 13100 = 30.00% exact, confirming **linear 16-bit BE encoding with max value `0x332C`** (i.e. ~40% of `0xFFFF`'s range). The 2026-04-24 doc's "intensity 100 = `33 2c`, intensity 0 = `00 00`" anchors are fully consistent with this scaling. PitHouse pushes a `0x0A 0x01` snapshot on connect and an immediate write on each slider drag — no caching, no Apply button needed for this slider.

#### Gear-shift feedback is firmware-driven (confirmed 2026-05-13)

Direct test: 4 gear shifts performed with engine vibration active, then a second test of "many shifts" with engine vibration intensity set to 0 to silence the dominant `0x0A 0x05` stream. **In both tests, gear shifts produce zero host→device CDC traffic.** Per-shift windows are byte-for-byte identical to steady-state idle on every frame signature. No new frame types appear; no rate change on existing streams.

This rules out paths 1 (group `0x43` push to AB9), 2 (HID-OUT on EP `0x03`), and 3 (wheelbase relay) for shift-triggered feedback. The AB9 detects gear changes via its own internal mechanical sensor, emits the new gear as a joystick button press on HID IN, and **fires its stored vibration pattern + notchiness + damping pattern from firmware** — all without host involvement. **PitHouse pushes the slider config (`0x0A 0x01`) once per slider change, and the AB9 firmware does all the rest.**

##### No analogue to wheelbase `cmd 0x76` shift-trigger needed

The wheelbase has a two-command pattern for gear-shift vibration: cmd `0x2E` (Group 0x29) is the stored intensity, and cmd `0x76` (Group 0x2D, `76 00 01` fire-and-forget) is a per-shift trigger that the SimHub plugin fires from game telemetry — needed because paddle-shifters are just buttons with no native shift semantics. **The AB9 needs no such trigger** because its H-pattern lever has a built-in mechanical engagement sensor; the device knows when a shift happened before the host does (the HID joystick-button event is the *output* of that internal detection, not its trigger). Searching all AB9 captures (the 2026-04-24 PitHouse-only set plus the 2026-05-13 PitHouse+AC session) shows zero short fire-and-forget commands targeting AB9 dev 0x12 during any shift event, and no SimHub plugin or PitHouse code path is observed to fire one. Conclusion: **the AB9 protocol surface intentionally has no host-side shift-trigger command**. The plugin's AB9 device manager correctly omits a `SendShiftEvent` method (see `Devices/MozaAb9DeviceManager.cs`); attempting to add one would have nothing to fire.

### FFB session-init handshake decoded (2026-05-15, full-session pass)

Full-session decode against `sim/logs/ab9-game-20260513.jsonl` (942,634 frames, 40.9 min) via `tools/ab9-decode-session`. The session-connect FFB handshake before any streaming is **exactly**:

```
t_rel   dir  payload (hex)                        meaning
0.0822  h2b  20 12 0e 02                          ffb-init type 2
0.0823  h2b  20 12 0e 01                          ffb-init type 1
0.0823  h2b  20 12 07 03                          alloc effect type 0x03  → b2h 07 01  (slot idx 0x01)
0.0881  h2b  20 12 07 09                          alloc effect type 0x09  → b2h 07 02  (slot idx 0x02)
0.0948  h2b  20 12 07 09                          alloc effect type 0x09  → b2h 07 03  (slot idx 0x03)
0.0989  h2b  20 12 07 01                          alloc effect type 0x01  → b2h 07 04  (slot idx 0x04)
0.1029  h2b  20 12 07 04                          alloc effect type 0x04  → b2h 07 05  (slot idx 0x05)
0.1106  h2b  20 12 07 01                          alloc effect type 0x01  → b2h 07 06  (slot idx 0x06)
0.1147  h2b  20 12 13 00 00                       commit (mask = 0x0000)
0.1147  h2b  20 12 0a 01 0f 5a … 0e 00 64 04 …    vib-config snapshot (intensity = 0x0f5a / 30 %)
```

The b2h ACK from the 0x07 alloc requests returns a **1-byte index** (0x01..0x06 sequential) — this is the *internal effect handle on the device side*, completely separate from the 16-bit "slot ID" used in `0x0A 0x05` streaming frames (which are host-side DirectInput effect handles). The two never appear together in any binding frame; the device firmware must internally map the streaming-frame slot ID through its own routing logic. **For plugin replication, the 0x07/0x0E/0x13 handshake is purely a session-warm-up; we never need to thread the returned 1-byte indices into later streaming frames.**

Effect-type bytes in the six 0x07 allocations: `03, 09, 09, 01, 04, 01`. The repeated allocations of the same type (two `09`s, two `01`s) suggest a "request N slots of this kind" pattern. No new alloc frames appeared anywhere later in the session — slots are allocated once at connect and reused for the entire session.

The capture also begins with the PitHouse-style identity probe cascade (groups `0x09`, `0x04`, `0x06`, `0x02`, `0x05`, `0x07`, `0x0f`, `0x11`, `0x08`, `0x10` — identical to the current plugin's probe except the plugin's order is `09 → 02 → 06 → 08 → 11` only, missing 04/05/07/0f/10) followed by **stored-setting reads on `Group 0x1E` with single-byte cmd payload**, then the FFB handshake.

### Read group is `0x1E` (single-byte cmd), not `0x1F` (2026-05-15)

The 2026-04-24 USB capture analysis above mapped slider WRITES on `Group 0x1F` with payload `<cmdId> 0x00 <value>` (3-byte). The 2026-05-13 session capture proves READS use a **different group with a different payload shape**:

```
Write:  7E 03 1F 12 <cmdId> 00 <val>          (3-byte payload)
Read:   7E 01 1E 12 <cmdId>                    (1-byte payload — JUST the cmd-id)
Resp:   7E 03 9E 21 <cmdId> <val_hi> <val_lo>  (3-byte payload — cmd + BE 16-bit value)
```

Read group `0x1E` toggles to `0x9E` for the response (response bit set). Sliders return **2-byte BE values** (e.g. `00 64` = 100, `00 23` = 35). Analog axis cmds (`0xD7`, `0xD8`) use the full 16-bit range (`66 e7`, `80 01`).

Frequencies during a 40-min session: PitHouse polls 0x1E at **66 Hz** continuously — far more than just a one-shot read on connect. The plugin's current `MozaCommandDatabase` registers `ab9-*` commands with `ReadGroup = WriteGroup = 0x1F` and 1-byte payload, so its read frames have the wrong group AND wrong shape; the real device will not respond to them, which is why the "Probing → AB9 connected" status latch never flips on the AB9 panel.

### Streaming sub-stream schemas (2026-05-15)

Full-session decode resolves the doc's previous "open schema questions" below:

#### `0x0B 0x02 / 0x0B 0x03` engine-pulse pair — 22 byte payload

```
0B XX [00 00 00 00] [pp pp] [pp pp] [00 00 00 00 00 00] [FF FA] 04 [aa aa] [dd] [00 00]
   sub  4 zero pad   ╰─ phase counter (16-bit BE, duplicated) ─╯   const   tag amp16  duty pad
```

- `XX = 0x02` (pulse-on) or `0x03` (pulse-off). Pulses come in tightly-spaced ON/OFF pairs (sub-ms apart) that share the same phase counter value.
- **Phase counter (offset 6-7 = offset 8-9, duplicated within frame)** is a 16-bit BE counter that advances monotonically across the session; advance per pair scales with RPM (small advance at idle, large advance at redline). **It is NOT a bipolar envelope sample** — the duplicated 16-bit field was the same value, not two independent samples. The duplication is likely a "phase / phase-mirror" or "expected / actual" position pair the device cross-checks.
- `FF FA` at offset 16-17: constant signed-16 envelope param (-6 or near-max unsigned). Unchanged across all observations.
- Tag byte `0x04` at offset 18: matches the same tag used in `0x0A 0x05`. Constant.
- **Amplitude16 (offset 19-20)**: `23 28` = 9000 in `0x0B 0x02`, `00 00` in `0x0B 0x03`. So 0x0B 0x03 is literally "this pulse with amplitude = 0" — encoding the OFF half of a square-wave pulse as a separate frame rather than a single-frame on/off envelope.
- **Duty byte (offset 21)**: `0x28 = 40` in 0x0B 0x02, `0x00` in 0x0B 0x03. Likely the device-side mixer level for this slot.

Implementation rule: monotonic phase counter that advances per-pulse-pair (RPM-driven cadence drives the rate); amplitude/duty derived from intensity slider; constants verbatim.

#### `0x08 0x04 / 0x08 0x06` low-rate signed-pair — 11 byte payload

```
08 XX [mm mm] [00 64] 04 [00 00 00 00 00]
   sub  ╰ 16-bit signed BE  const tag  trailing zeros
```

Decoded from a typical frame sequence (timestamps in seconds):

| t_rel  | 0x08 0x04 (mm) | 0x08 0x06 (mm) | magnitude |
|---|---|---|---|
| 53.6041 | `ff e8` (-24)  | `00 18` (+24)  | 24  |
| 53.6266 | `fe d9` (-295) | `01 27` (+295) | 295 |
| 53.6353 | `fe 5d` (-419) | `01 a3` (+419) | 419 |
| 53.6672 | `fd fb` (-517) | `02 05` (+517) | 517 |

`0x08 0x04` and `0x08 0x06` are a **signed bipolar pair** — they carry equal-magnitude opposite-sign values, paired sub-ms apart in time. The magnitude tracks a slow engine-cycle / RPM-position signal (monotonically advancing in this window).

The `00 64 04` at offset 4-6 of the args is constant: `0x0064 = 100` (likely an envelope amplitude/scale), `0x04` is the tag matching `0x0A 0x05` and `0x0B 0x02/03`.

Frequency: ~0.35 Hz across the session (sparse). Likely fires per engine cycle (firing-stroke phase signal), independent of the higher-rate `0x0B` pulse train.

Implementation rule: state-driven by an RPM-cycle phase accumulator; emit the pair when the accumulator crosses a threshold; magnitude = current phase position.

#### `0x0D 0x01` (newly observed, was missing from the 2026-05-13 doc) — 3 byte payload

```
0D 01 [01]
```

3-byte frame, payload `0x0D 0x01 0x01`. First occurrence at t_rel = 53.60 s (not at session start). 241 frames over the session, ~0.10 Hz. Same shape as `0x0D 0x02/03/05`. Purpose unresolved; for plugin replication, emit at the same low rate as `0x0D 0x02/03` since the sim accepts it with the generic FFB ACK.

#### Slot-ID table observed (2026-05-15 update)

```
slot      count     first  last   period range
0x1996    80,286    112.27 1363.76  0x050032 .. 0x6f0457
0x1478    63,467    158.51 2050.01  0x1200b9 .. 0xc807d0
0x0000    46,584     26.65 2453.56  0x00008a .. 0xa60682     (silent keepalive)
0x0624    15,204   1096.26 2050.16  0x050032 .. 0x8e0594
0x0ccb     6,613     48.82  878.13  0x1300c4 .. 0xa60682
0x0c48       360    862.68 2050.10  0x1100b2 .. 0x8e0594
… 33 more transient slots, each <100 frames, with `0x0106` stride increments
```

The 33 transient slots with `0x0106` (262) stride strongly suggest **Windows DirectInput allocates effect handles in 262-byte increments** — these are PitHouse's session-internal handle churn as it reconfigures effects during slider drags, **not protocol-meaningful slot indexes from the device side**.

For plugin replication, a **fixed slot table** is sufficient: `0x1996` for primary engine-vib, `0x1478` for secondary harmonic, `0x0000` for silent keepalive. The sim ACKs every slot ID generically and PitHouse's slot choices are random session-to-session anyway — the device firmware ultimately routes through its own 0x07-allocated effect indices, not the streaming-frame slot IDs.

### Remaining open schema questions (post-2026-05-15)

These survive the full-session decode and are likely not blockers for plugin replication:

- The `0x0D 0x05` rate model — sub-linear with `(freq × RPM × intensity)`, intensity-sign flips at idle vs redline. For replication, drive it from the same per-RPM-cycle phase accumulator as `0x0B 0x02/03` and `0x08 0x04/06`; the rate will fall out of the cadence naturally without needing a closed-form rate model.
- The eight zero bytes between slot ID and period in `0x0A 0x05`, and the four trailing zeros — static across all observations; treat as fixed protocol padding.
- The `FF FA` constant in `0x0B 0x02/03` and the `0E 00 64 04` tail in `0x0A 0x01` vib-config — neither varies across intensity/RPM sweeps; treat as fixed protocol padding for replication.
