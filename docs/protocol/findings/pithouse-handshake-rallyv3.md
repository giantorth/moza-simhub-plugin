# PitHouse → Wheelbase Handshake (CSP wheel, Rally v3 dash)

Captured live with PitHouse already running and Assetto Corsa active. Wheel
config: CSP pedals + R5 Black wheelbase + W17 (Rally v3) dash rim.

## Capture

- File: `sim/logs/bridge-20260503-112940.jsonl`
- Bridge start: `t=1777832980.150` (2026-05-03 11:29:40 local)
- Base port: `/dev/ttyACM0`  Gadget port: `/dev/ttyGS0`
- ~270 fps steady, 0 bad checksums
- Direction: `h2b` = PitHouse → wheelbase, `b2h` = wheelbase → PitHouse

## Phase 0 — base idle broadcast (pre-host)

Wheelbase streams its periodic frames before any host traffic. Lines 1–22 of
the JSONL.

| dir | grp | dev | payload | role |
|-----|-----|-----|---------|------|
| b2h | 0xDA | 0xB1 | 20×0x00 | ADC pad / IMU placeholder |
| b2h | 0xDD | 0xB1 | `010000` | base keepalive |
| b2h | 0xA5 | 0x91 | `010000` / `020000` / `030000` | CSP pedal channels (throttle / brake / clutch) |
| b2h | 0xC0 | 0x71 | `1f03 01 0000ff00` | wheel-status preamble |

## Phase 1 — host probe (T+0.251)

PitHouse opens the port, sends three identity probes to the descriptor device
`0x12`. JSONL lines 23–25.

```
h2b grp=0x04 dev=0x12 payload=00000000
h2b grp=0x02 dev=0x12 payload=00
h2b grp=0x05 dev=0x12 payload=00000000
```

## Phase 2 — descriptor reply (T+0.301)

Base answers with response groups (0x80 bit set on probe groups). JSONL lines
27–31.

```
b2h grp=0x89 dev=0x21 payload=0001                              ack
b2h grp=0x84 dev=0x21 payload=01021208                          device class
b2h grp=0x86 dev=0x21 payload=410021001851333135363734          HW IDs (Q3156734)
b2h grp=0x82 dev=0x21 payload=02
b2h grp=0x85 dev=0x21 payload=01025400
```

## Phase 3 — ASCII model strings (T+0.401)

Wheelbase advertises model name. JSONL lines 45, 47, 48.

```
b2h grp=0x87 dev=0x21 "R5 Black # MOT-1"   wheelbase model
b2h grp=0x8F dev=0x21 "RS21-D05-MC WB"     model code
b2h grp=0x91 dev=0x21 0401                 flag
```

## Phase 4 — wheel + variant strings (T+0.451–0.65)

JSONL lines 51, 52, 55–60, 69, 72.

```
b2h grp=0x88 dev=0x21 "RS21-D05-HW BM-C"   HW rev
b2h grp=0x90 dev=0x21 "6/0Vs3jv4imz7A2v"   UUID 1
b2h grp=0x87 dev=0x71 "W17"                wheel rim id (CSP / Rally v3)
b2h grp=0x87 dev=0x21 "-V03"               version V03
b2h grp=0x88 dev=0x21 "U-V10"              firmware
b2h grp=0x90 dev=0x21 "KO3WeB/OdpCpuVL1"   UUID 2
b2h grp=0xAB dev=0x31 040ed8 / 050cd8 /
                       06094b / 010003     CSP pedal calib triplets
```

`dev=0x71` confirms the rim identity (W17 = Rally v3). `dev=0x21` carries
wheelbase identity. `dev=0x31` carries CSP pedal calibration.

## Phase 5 — stream subscribe (T+0.50)

PitHouse turns on telemetry channels via dev `0x18`. JSONL lines 38–42, 67,
68.

```
h2b grp=0x07 dev=0x18 01     enable channel 7
h2b grp=0x0F dev=0x18 01     enable channel 15
h2b grp=0x11 dev=0x18 04     enable channel 17, mode 4
h2b grp=0x08 dev=0x18 01     enable channel 8
h2b grp=0x10 dev=0x18 00     channel 16 off
```

## Phase 6 — dash select (T+0.65)

PitHouse selects rim/dash devices with `grp=0x43 cmd=00`. JSONL lines 81–83.

```
h2b grp=0x43 dev=0x14 00     reserved/unused dash slot
h2b grp=0x43 dev=0x15 00     reserved/unused dash slot
h2b grp=0x43 dev=0x17 00     ACTIVE: dev 0x17 = Rally v3 dash
```

`dev=0x17` is the **Rally v3 dash address**. Every steady-state dash drive
frame targets this device.

## Phase 7 — informational firmware warning (T+0.70)

Base prints a log line over the wire. Not fatal. JSONL line 87.

```
b2h grp=0x0E dev=0x21 "[WARN]serial_cmd_pull_main.c:1450 Unexpected cmd: 100"
```

PitHouse sent a probe the firmware does not recognize. Bridge passes through;
PitHouse continues normally.

## Phase 8 — re-enumerate (T+0.95)

PitHouse repeats the probe → descriptor → strings cycle (lines 94–108). The
handshake is **idempotent and repeating**, not one-shot. Useful for re-attach
without bridge restart.

## Phase 9 — steady state (T+~1.0)

Steady traffic settles at ~270 fps. Shapes below.

### Dash drive (h2b → dev 0x17, PitHouse drives Rally v3)

| grp | cmd | size | rate | role |
|-----|-----|------|------|------|
| 0x43 | 7c00 | 10 B | ~47 Hz | shift lights / RPM bar |
| 0x43 | 7d23 | 22 / 26 / 19 B | ~25 Hz | packed dash payload — byte 6 = session slot, length = dash family (see Phase 10) |
| 0x43 | 7c1e | 15 B | low | LED / segment frame |
| 0x43 | 7d23…0f | 18 B | one-shot | dash mode-set |
| 0x43 | fc00 | 10 B | ~2 Hz | dash status / heartbeat |
| 0x40 | 1f03 | 11 B | ~25 Hz | LED zone write (all dashes) |
| 0x41 | fdde | 11 B | ~40 Hz | wheel keepalive (rate stable across dashes) |
| 0x3F | 1900 | 22 B | low | mode/screen frame (rally + grids only, absent in core/nebula) |
| 0x2D dev 0x13 | f531 | 11 B | ~40 Hz | pedal / wheel vibration |

### Telemetry return (b2h)

| grp | dev | cmd | role |
|-----|-----|-----|------|
| 0xDA | 0xB1 | — | ADC / IMU pad (zero when idle) |
| 0xDD | 0xB1 | 0100 | base keepalive |
| 0xA5 | 0x91 | 01/02/03 | CSP pedal samples (3 channels) |
| 0xC3 | 0x71 | 7c00 / fc00 | wheel input replies |
| 0xC0 | 0x71 | 1f03 | wheel-status |

## Phase 10 — Dash template switch (Rally v3 ↔ Rally v4)

Captured live by switching dashboards in PitHouse mid-stream and diffing
shape/payload distributions.

**Captures referenced**:
- `sim/logs/bridge-20260503-112940.jsonl` — v3 only (62796 frames, 251 s)
- `sim/logs/bridge-20260503-113353.jsonl` — v3 then v4 (37729 frames, 132 s)
- `sim/logs/bridge-20260503-113616.jsonl` — v4 then v3 (165160 frames, 496 s)
- `sim/logs/bridge-20260503-115840.jsonl` — multi-switch verification: rally v3 → grids → core → nebula → rally v3 → rally v4 (142309 frames, 463 s)

### Key finding: switch is software-only

- **No physical re-enumeration**. Wheel rim ID stays `"W17"` across switch.
  Same `dev=0x71`, same `dev=0x17` for dash. No new descriptor strings, no
  base log warning, no probe storm.
- The PitHouse plugin runs the same idempotent probe → descriptor cycle
  (Phase 1–4) every ~500 ms regardless of dash. The switch does not trigger
  a fresh enumeration.
- Discriminator lives in **payload bytes** + **shape distribution** of
  existing groups, not in new shapes or addresses.

### Marker 1: `7d23` packed dash payload, byte 6 (switch detector)

The `h2b grp=0x43 dev=0x17 cmd=0x7d23` frame has fixed prefix
`7d 23 32 00 23 32` followed by a varying byte 6, separator `0x20`, then
telemetry data of variable length.

Byte 6 is a **session-scoped slot index** that PitHouse increments on every
dash switch. It is **NOT a fixed dash-type ID** — the same dash takes a
different byte-6 value depending on prior switches in that session.

Settled byte-6 values observed:

| Capture | Phase | Byte 6 |
|---------|-------|--------|
| 112940 | rally v3 (whole) | `0x0e` |
| 113353 | rally v3 → rally v4 | `0x0e` → `0x1d` |
| 113616 | rally v4 → rally v3 | `0x2d` → `0x3c` |
| 115840 | rally v3 (resumed) | `0x05` |
| 115840 | grids | `0x15` |
| 115840 | core | `0x1f` |
| 115840 | nebula | `0x25` |
| 115840 | rally v3 (re-selected) | `0x2a` |
| 115840 | rally v4 | `0x3f` |

Same dash, different sessions → different byte-6. Same dash selected twice
in one session → different byte-6 each time. Within a single dash phase the
value is rock-stable (98–100 % of frames).

Byte 6 always **steps monotonically upward** on switch (never repeats a
prior value within the same session). Useful as a simple
"switch-just-happened" trigger.

### Marker 2: `7d23` payload length (dash family)

Length of the `7d23` frame is a **family-level** classifier, stable per dash:

| Length | Dashes | Notes |
|--------|--------|-------|
| 22 B | rally v3, rally v4 | "rally" family payload layout |
| 26 B | grids, core | grid/core family — wider field set |
| 19 B | nebula | shorter payload |

Cross-checked against multi-switch capture (115840):

| Phase | dominant `7d23` length | secondary lengths |
|-------|------------------------|-------------------|
| pre (rally v3) | 22 B (2018) | 18, 26 (rare) |
| grids | 26 B (3999) | 31 (rare) |
| core | 26 B (639) | — |
| nebula | 19 B (486) | — |
| rally v3 | 22 B (185) | — |
| rally v4 | 22 B (398) | — |

Combine length + byte 6 to identify a dash within a session. Length alone
collapses families (can't distinguish v3 from v4, or grids from core).

### Shape exclusivity is weak across full dash set

Earlier two-dash analysis claimed many shapes were "v3-aligned" or
"v4-aligned." Re-checking against the 6-dash capture (115840) those claims
do not hold — the rates differ across all 6 dashes but no shape is reliably
unique to a single dash.

Findings that **did** hold up under 6-dash testing:

| Signal | Behavior |
|--------|----------|
| `grp 0x40 dev 0x17 cmd 1e00/1e01/1e03/1f03` | present in **all** dashes at varying rates — NOT a dash discriminator |
| `grp 0x3F dev 0x17 cmd 1900` | present in pre, grids, rally v3, rally v4 — **absent** in core and nebula. Partial family signal: appears under "rally" + "grid" lineage, not under "core/nebula" |
| `grp 0x0E dev 0x17 cmd 0000/0001/000f` | present in pre, grids, nebula, v3, v4 — **absent** only in core. Possibly a window-size artifact (core window was 35 s) |
| `grp 0x41 dev 0x17 cmd fdde` | always ~40 Hz regardless of dash. Earlier "v4 = 2× rate" claim was caused by short-window sampling noise, not a real difference |

Conclusion: **shape distribution alone cannot identify the active dash**.
Use byte 6 + length on `7d23`.

### Switch detection heuristic (verified)

1. Watch `h2b grp=0x43 dev=0x17 cmd=0x7d23` byte 6.
2. **Switch in progress** when consecutive frames carry different byte 6
   values, or when the dominant value in a 5 s window changes.
3. **Switch settled** when byte 6 holds steady (>95 % of frames in window)
   for ≥ 10 s.
4. After settle, read `7d23` length to identify dash family
   (22 = rally, 26 = grids/core, 19 = nebula).

User-typed switch announcements lag the actual switch by **5–25 s** (typing
delay). Detect from data, not human markers.

## Phase 11 — `28:xx` semantics open

Plugin's `SendChannelConfig` already polls `28:00` + `28:01` once at preamble
end. PitHouse polls them at ~7 Hz throughout the session — meaning they
matter more than a single read. **What they actually carry is unconfirmed.**

Capture observation (`bridge-20260503-115840.jsonl`, multi-switch):

- `28:00` reply byte 5 oscillates `00 / 01 / 0b / 18 / 19 / 1a / 20 / 26 / 2f / 64`
  across all dashes. No clean correlation to dash phase.
- `28:01` reply byte 5 mostly `00` / `01` / `0b`.

Dissector mapping (`docs/moza_dissector.lua:155–157`) and sim behavior
(`sim/wheel_sim.py:4785–4810`) are **prior guesses**, not authoritative —
the whole reason for this capture work was that those sources didn't
explain the observed wire behavior. Treat their `Get Dashboard Mode` /
`Get Active Page` labels as hypotheses, not facts.

Open questions for follow-up:

- What real PitHouse interaction triggers a change in `28:00` reply byte?
  (Touch the wheel rotary? Press a wheel button? Switch dash via PitHouse?)
- What does the byte mean in steady state?
- Why does PitHouse poll so frequently — what UI element depends on it?

Until answered, plugin should not invent fields based on assumed meaning.

## Useful capture commands

```
mcp__bridge-linux__bridge_log_path           # current jsonl path + size
mcp__bridge-linux__bridge_counters           # frame counts / uptime
mcp__bridge-linux__bridge_histogram top=30   # shape ranking
mcp__bridge-linux__bridge_recent count=N direction=h2b|b2h
```

### Shape-diff one-liner (compare two time windows in same capture)

```bash
LOG=sim/logs/bridge-NNNNNN.jsonl
START=$(head -1 $LOG | awk -F'"t":' '{print $2}' | awk -F',' '{print $1}')
A=$(mktemp); B=$(mktemp)
# window A: 30-90s
grep '"dir":"h2b"' $LOG | awk -F'"t":' -v s=$START '{split($2,a,","); if(a[1]-s>30 && a[1]-s<90) print}' \
  | grep -oE '"grp":[0-9]+,"dev":[0-9]+,"payload":"[0-9a-f]{0,4}' | sort | uniq -c > $A
# window B: 180-300s
grep '"dir":"h2b"' $LOG | awk -F'"t":' -v s=$START '{split($2,a,","); if(a[1]-s>180 && a[1]-s<300) print}' \
  | grep -oE '"grp":[0-9]+,"dev":[0-9]+,"payload":"[0-9a-f]{0,4}' | sort | uniq -c > $B
# A-only / B-only / biased shapes
join -v1 -1 2 -2 2 -o 1.1,1.2 <(sort -k2 $A) <(sort -k2 $B)
join -v2 -1 2 -2 2 -o 2.1,2.2 <(sort -k2 $A) <(sort -k2 $B)
```
