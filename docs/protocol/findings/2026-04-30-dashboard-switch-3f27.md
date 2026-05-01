# Dashboard switch wire signals (session 0x02 FF-record + `0x3F 27:NN`)

**Date:** 2026-04-30 (updated 2026-05-01)
**Captures:**
- `wireshark/csp/startup, change knob colors, change dash several times, delete dash.pcapng`
- Bridge capture `sim/logs/bridge-20260430-210453.jsonl` (PitHouse → real CSP wheel)
**Hardware:** CSP firmware (R5 base + W17 wheel) — Pithouse 1.2.6.17
**Status:** Wire format verified from capture + bridge. Slot indexing verified against live wheel (plugin sent slot, wheel displayed correct dashboard). Tier-def re-send timing verified from bridge capture. Plugin implementation functional but still being refined.

## Summary

Two distinct host→wheel signals observed during dashboard switches:

1. **FF-record on session 0x02** — the **primary** "set active dashboard" command.
2. **`0x3F 0x17 27:[page]` direct frame** — **secondary** per-page fingerprint update (state sync, not the switch trigger).

---

## Mechanism 1 — FF-record on session 0x02 (primary, verified)

### Wire format

25-byte payload sent inside a SerialStream page-data frame on **page 0x02**:

```
SerialStream wrapper:
  7c 00 02 01 [seq:LE16] <payload...>

Payload (25 bytes):
  ff
  0c 00 00 00          // data_size = 12 (LE32)
  [data_crc:LE32]      // CRC32 of (field1 || field2 || field3)
  [field1:LE32]        // = 4 for switch ops; = 7 at session start (different cmd)
  [field2:LE32]        // 0-based slot index into configJsonList
  [field3:LE32]        // = 0
  [body_crc:LE32]      // CRC32 of (ff || data_size || data_crc || field1 || field2 || field3)
```

CRC32 = standard polynomial `0xEDB88320`, init `0xFFFFFFFF`, XOR-out
`0xFFFFFFFF` (Python `zlib.crc32` / Java `java.util.zip.CRC32`).

### Slot index source

Slot = **0-based** index into `configJsonList` (the **alphabetical**
dashboard name list from session 0x09 state push), **NOT** into
`enableManager.dashboards` (which is insertion/upload order and has a
different sequence).

**Verified 2026-04-30 against live wheel:** sending slot=1 activated
`configJsonList[1]` ("Grids"), not `enableManager.dashboards[1]`
("Rally V5"). The two lists have different orderings.

Example from live wheel with 12 dashboards:
```
configJsonList (alphabetical):
  [0] Core       [1] Grids      [2] Mono
  [3] Nebula     [4] Pulse      [5] Rally V1
  [6] Rally V2   [7] Rally V3   [8] Rally V4
  [9] Rally V5   [10] Rally V6  [11] asdf

enableManager.dashboards (insertion order):
  [0] Rally V1   [1] Rally V5   [2] Rally V2
  [3] Rally V3   [4] Rally V6   [5] Rally V4
  [6] Core       [7] Mono       [8] Pulse
  [9] asdf       [10] Nebula    [11] Grids
```

### Wheel response sequence

After receiving the FF-record the wheel:

1. FC-acks the frame on session 0x02
2. Echoes the record back on session 0x02 device→host
3. Re-pushes the channel catalog on session 0x01 (new dashboard's
   channel URLs, using `\x01` prefix shorthand — e.g. `\x01Rpm`
   instead of `v1/gameData/Rpm`)
4. Re-pushes binding catalog on session 0x01 (enable/tier/end TLV records)

### Tier-def re-send timing (verified from bridge capture)

**PitHouse sends tier-def ~800ms after the FF-record.**

During the 800ms gap, the wheel pushes its new channel catalog on
session 0x01. PitHouse uses this fresh catalog for correct channel
indices in the tier-def.

**Critical:** PitHouse does NOT re-send the tag 0x07/0x03 preamble on
subsequent tier-defs. Preamble is sent ONCE at session connect. All
re-sends (dashboard switch) use only enable/tier/end records. Sending a
duplicate preamble causes the wheel to reject the tier-def.

**Implementation lesson (2026-05-01):** The host must start buffering
session 0x01 catalog data at the moment the FF-record is sent, not
800ms later when the tier-def is built. The wheel's catalog push
completes within ~300ms of the FF-record. If the host defers buffer
accumulation to renegotiation time, all catalog chunks have already
arrived and been discarded — causing the tier-def to reference stale
channel indices from the previous dashboard.

Bridge capture evidence (3 consecutive switches):
```
t=613.150  FF-record slot=6
t=613.957  tier-def: ENABLE×3 + TIER×2 + END (no preamble)  [+807ms]

t=615.570  FF-record slot=5
t=615.974  tier-def: TIER×2 + END + ENABLE×2 + TIER×2 + END  [+404ms]

t=617.282  FF-record slot=4
t=617.988  tier-def: ENABLE×2 + TIER + END                   [+706ms]
```

### Captured records

```
t=47.89s   fn=97411   field1=7  field2=3   (startup — init, NOT a switch)
t=212.10s  fn=558353  field1=4  field2=0   (switch to configJsonList[0])
t=214.91s  fn=566115  field1=4  field2=1
t=223.13s  fn=588199  field1=4  field2=0
t=225.14s  fn=593671  field1=4  field2=2
t=226.79s  fn=598193  field1=4  field2=3
t=228.55s  fn=603069  field1=4  field2=4
t=238.74s  fn=631645  field1=4  field2=10
```

---

## Mechanism 2 — `0x3F 0x17 27:[page]` direct write (secondary)

Per-page binding state update observed alongside the FF-record path.
Likely state sync rather than the switch trigger. See
[`../channel-config/group-0x40-burst.md`](../channel-config/group-0x40-burst.md)
§ 27:NN for wire format details.

### Wire format

```
write : 7e 06 3f 17 27 [page] [flag:1] [fingerprint:3] [csum]
read  : 7e 03 40 17 27 [page] 00
reply : 7e 06 c0 71 27 [page] [flag:1] [fingerprint:3] [csum]
```

`page` ∈ {0, 1, 2, 3}. `flag` byte: `0x00` = primary state, `0x01` =
alternate state (semantics TBD). `fingerprint` = 24-bit opaque ID,
wheel-assigned, NOT derivable from any visible dashboard field.

---

## Wheel channel catalog format post-switch

After receiving the FF-record, wheel re-pushes its channel catalog on
session 0x01 using a **shortened URL prefix**: byte `0x01` followed by
the URL suffix (e.g. `\x01Rpm` = `v1/gameData/Rpm`). Parser must
accept both `v1/gameData/` prefix and `\x01` prefix and normalize to
the full URL form for catalog matching.

---

## End-marker u32 semantics

The tag 0x06 end-marker in tier-def carries a u32 value that varies
across switches: 0, 9, 21, 30, 42, 54, 76, 90, 96, 104 observed.
Not a channel count. Likely a wheel-internal cumulative slot counter.
Wheel accepts any value — plugin uses cumulative channel count as
placeholder, which works.

---

## Plugin implementation notes

**Recommended flow for dashboard switch:**

1. Send FF-record on session 0x02 (slot = 0-based `configJsonList` index)
2. Wait ~800ms for wheel to push new catalog on session 0x01
3. Parse catalog (accept `\x01` prefix URLs)
4. Send tier-def on session 0x01 (NO preamble, just enable/tier/end records)
5. Send 0x40 channel-config burst

**Do NOT:**
- Send tier-def immediately (stale catalog → wrong indices)
- Re-send preamble tags 0x07/0x03 (only on initial session connect)
- Close or re-open any sessions
- Upload .mzdash or send LVGL frames

---

## Doc cross-refs

- [`../channel-config/group-0x40-burst.md`](../channel-config/group-0x40-burst.md) § 27:NN — `3F 27:NN` wire format
- [`../../usb-capture/payload-09-state-re.md`](../../../usb-capture/payload-09-state-re.md) — SET-side signal history
- [`../tier-definition/handshake.md`](../tier-definition/handshake.md) — startup tier-def phases + in-game re-send
