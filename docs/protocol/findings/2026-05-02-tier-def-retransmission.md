# Tier-def retransmission: PitHouse blind-retransmits, wheel never acks

**Date:** 2026-05-02
**Captures:** All PitHouse bridge captures in `sim/logs/bridge-*.jsonl`
**Hardware:** CSP firmware (R5 base + W17 wheel) — PitHouse 1.2.6.17

## Finding

The wheel **never sends FC-ack records on session 0x01** (tier-def /
management session). PitHouse **blind-retransmits** every session 0x01
data chunk regardless, achieving ~10.7× retransmission per unique chunk.

The plugin's `SessionRetransmitter` is ACK-based: it queues chunks and
retransmits at 200 ms intervals until acked. Since the wheel never acks
session 0x01, the retransmitter **should** keep retransmitting — but
actual behavior depends on nothing else clearing the queue first.

## Evidence

### Bridge capture `bridge-20260501-070212.jsonl`

```
Session 0x01 h2b (PitHouse → wheel):
  unique seqs:           161
  total chunks:         1725
  avg per chunk:       10.7×
  max per chunk:         37×

Session 0x01 b2h (wheel → PitHouse):
  FC-ack records:          0
  data records:          183
```

Zero FC-acks confirmed across all bridge captures examined. PitHouse
never receives acknowledgement for tier-def chunks yet continues
retransmitting for the duration of the session.

### Plugin wire trace `moza-wire-20260502-210703.jsonl`

```
Session 0x01 h2b (plugin → wheel sim):
  unique seqs:           140
  total chunks:          140
  avg per chunk:        1.0×
  retransmissions:         0
```

Plugin sends each chunk exactly once. Zero retransmissions despite zero
acks from the wheel simulator.

## Root cause for switch failure (hypothesis)

Dashboard switching requires the wheel to accept a new tier-def after
the FF-SWITCH record. The initial tier-def works because the wheel
processes data chunks during the startup window. After a switch, the
wheel may require repeated exposure to tier-def chunks (10× or more) to
latch the new channel configuration. With the plugin sending each chunk
once, the wheel may never process the post-switch tier-def.

## Fix

Change the retransmitter from ACK-gated to blind retransmission for
session 0x01 tier-def chunks, matching PitHouse behavior:

- Retransmit each chunk ~10× at ~200 ms intervals (2 seconds total)
- Do not wait for FC-ack on session 0x01
- ACK-based retransmission can remain for other sessions if needed

## Cross-references

- [`2026-04-30-dashboard-switch-3f27.md`](2026-04-30-dashboard-switch-3f27.md) —
  switch flow and tier-def timing
- [`../sessions/chunk-format.md`](../sessions/chunk-format.md) — chunk
  framing and FC-ack format
