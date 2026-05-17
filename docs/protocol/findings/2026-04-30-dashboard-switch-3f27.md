# Dashboard switch wire signals (session 0x02 FF-record + `0x3F 27:NN`)

**Date:** 2026-04-30 (revised 2026-05-17 against fresh bridge captures)
**Captures:**
- `wireshark/csp/startup, change knob colors, change dash several times, delete dash.pcapng`
- `sim/logs/bridge-20260430-210453.jsonl` (PitHouse → real CSP wheel)
- `sim/logs/bridge-20260517-070054.jsonl` — 16 min, 5 user switches mixing wheel-initiated + PitHouse-initiated, R5 base + W17 wheel, PitHouse 1.2.6.17
- `sim/logs/bridge-20260517-080546.jsonl` — 13 forward wheel switches
- `sim/logs/bridge-20260517-081336.jsonl` — 14 backward wheel switches
- `sim/logs/bridge-20260517-082046.jsonl` — 10 wheel-side page changes within Grids

**Hardware:** CSP / Type02 firmware (R5 base + W17 wheel) — Pithouse 1.2.6.17
**Status:** Wire format verified across 42 user-input events. Slot indexing verified against live wheel. END u32 handshake semantics verified via `tools/tierdef-decode`. Multi-emission burst pattern verified. The originally-documented FF-record path is correct; canonical home for the full sequence (timing, multi-emission burst, END handshake, page changes, wheel-initiated case) is now [`../tier-definition/handshake.md`](../tier-definition/handshake.md) — this finding retains only the wire-format details + slot-indexing proof.

## Summary

The primary switch signal is the **FF-record on session 0x02** (kind=4 — see Mechanism 1 below). It is emitted by either side: the host (PitHouse / plugin UI) or the wheel (hardware control). For wheel-initiated switches the FF-record is preceded ~0.1 ms by a `b8 AA BB` event on `grp=0xC3 dev=0x71` (see [`../devices/wheel-0x17.md`](../devices/wheel-0x17.md) § Group 0x43).

The `0x3F 0x17 27:[page]` write documented in Mechanism 2 below is a per-page binding state update observed alongside the FF-record path. It is NOT the switch trigger.

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

### Tier-def re-send sequence

The full canonical tier-def re-send sequence lives in
[`../tier-definition/handshake.md`](../tier-definition/handshake.md)
§ "In-game dashboard switch and page change" — including timing
ranges, multi-emission burst pattern, END u32 handshake, flag-base
progression. See also `tools/tierdef-decode` for byte-level
verification against bridge captures.

Two protocol invariants worth highlighting here:

1. **Preamble (tag 0x07/0x03) is NEVER re-sent within a session.**
   It is emitted ONCE at session 0x01 connect. Subsequent tier-defs
   (dashboard switch, catalog growth, retransmit) carry only
   ENABLE / TIER / END records. Re-sending preamble causes the
   wheel to reject the tier-def.

2. **PitHouse does NOT re-parse the wheel's post-switch catalog
   push.** It builds tier-def from its own locally-cached mzdash
   channel metadata + the initial preamble catalog indices (which
   stay valid for the whole session). The wheel's post-switch
   catalog push is informational — it tells the host what the wheel
   thinks the dashboard's channels are, but PitHouse already knows.
   See [`../dashboard-upload/download-session-0x0b.md`](../dashboard-upload/download-session-0x0b.md)
   for how PitHouse acquires mzdash files at cold-start.

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

The tag 0x06 end-marker u32 is a **handshake echo from the wheel**,
NOT a host-invented value (verified 2026-05-17 via
`tools/tierdef-decode` against `sim/logs/bridge-20260517-070054.jsonl`).

The wheel pushes its own `0x06 04 00 00 00 <u32>` end-marker as the
FINAL record of its post-switch b2h sess=0x01 catalog stream
(~+1000 ms after kind=4, after the URL records). PitHouse echoes
that exact u32 on every tier-def emission of the burst. Mismatch =
wheel treats the tier-def as stale and does not commit widget
binding (observed symptom: physical test-pattern button renders
nothing post-switch).

Verified pairings from `bridge-20260517-070054.jsonl`:

| Switch | Wheel END (b2h push) | PitHouse echo (tier-def emissions) |
|--------|-----------------------|--------------------------------------|
| slot=10 Rally V5 | 42 at k4+1023 ms | 42 (#117, #118) |
| slot=2 Grids | 43 at k4+481 ms; 68 at k4+1484 ms | 43 (first emission), then 68 (11 retx) |

Across one full session END advanced `6 → 16 → 23 → 32 → 41 → 43 →
64` — monotonic, no derivable formula. The host's job is to track
the latest value the wheel pushed and echo it; the increment rule is
the wheel's internal business.

Plugin implementation: `Telemetry.Frames.ChannelCatalogParser.LastWheelEndMarker`
is updated as catalog chunks arrive; `TelemetrySender.SendTierDefinition`
reads it and passes to `TierDefinitionBuilder.BuildTierDefinitionMessage`
via `endMarkerCounter` parameter. All broadcasts inside one emission
share the same END value (matching PitHouse's same-END-across-broadcasts
pattern).

---

## Doc cross-refs

- [`../tier-definition/handshake.md`](../tier-definition/handshake.md) § In-game dashboard switch and page change — canonical re-negotiation sequence, multi-emission burst, END handshake
- [`../tier-definition/version-2-compact-vgs.md`](../tier-definition/version-2-compact-vgs.md) § Per-tier end-marker — wire-format spec for the END u32 echo
- [`../devices/wheel-0x17.md`](../devices/wheel-0x17.md) § Group 0x43 — `B8 AA BB` wheel-side input event (next/prev dash, next/prev page)
- [`../channel-config/group-0x40-burst.md`](../channel-config/group-0x40-burst.md) § 27:NN — `3F 27:NN` per-page binding state update (secondary signal)
- [`../../usb-capture/payload-09-state-re.md`](../../../usb-capture/payload-09-state-re.md) — SET-side signal history
