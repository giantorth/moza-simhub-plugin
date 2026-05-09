# PitHouse tier-def reference (Phase A read-only)

Source: bridge captures `sim/logs/bridge-20260503-{112940,113353,113616,115840}.jsonl`.
Decoded with `/tmp/tierdef_dump.py` (chunk reassembly + TLV parse on session
0x01, type 0x01 frames inside `h2b grp=0x43 dev=0x17 cmd=7c00`).

All facts here cite a capture+timestamp. Hypotheses without capture support
are flagged "open".

## TLV vocabulary observed

| tag  | size  | meaning                                           |
|------|-------|---------------------------------------------------|
| 0x07 | 4     | PROTO_VER (= 2 in every preamble seen)            |
| 0x03 | 0     | preamble terminator / config-zero marker          |
| 0x01 | 1+16N | TIER: flag byte + N × 16-byte channel records     |
| 0x00 | 1     | ENABLE-prior-flag (header position only)          |
| 0x06 | 4     | END_MARKER value (semantics partly open — below)  |
| 0x04 | n     | URL idx + ascii — **never seen in PitHouse traffic** |

Channel record: idx LE (4B), comp LE (4B), bw LE (4B), reserved (4B = 0).

## Confirmed structural facts

### Preamble appears once per session, not per message

In every capture the `tag=0x07 PROTO_VER` + `tag=0x03 size=0` pair appears in
exactly one message — the first tier-def of the session.

- `112940` t+0.5s seq=2..16 (init, 426B): preamble present
- `113616` t+0.66s seq=5..10 (init, 89B): preamble present
- `115840` t+0.05s seq=2..15 (init, 394B): preamble present
- `113353` first observed msg seq=118..134 (442B): no preamble — capture
  starts mid-session

No subsequent tier-def message in any capture re-emits the preamble.

> Plugin's existing "preamble-on-first-only" gate is correct. The session's
> revert of "preamble re-sent every msg" matches captures.

### Tier flags advance monotonically across the session, never reset on dash switch

Flag bytes within a session are a single global ascending counter, shared
across dashboards.

| Capture | Flag span observed | Notes |
|---------|--------------------|-------|
| 112940  | 0x00..0x10         | Rally V3 only, 17 distinct tier-defs |
| 113616  | 0x00..0x1f+        | V4 init then switch to V3 |
| 115840  | 0x00..0x24+        | 6-dash multi-switch |
| 113353  | 0x11..0x1f (mid)   | started after 17 tier-defs |

Flag-base does not snap to round numbers (no +8 alignment). It advances by
exactly the count of tiers declared in the previous emission.

> Plugin's `_nextFlagBase += profile.Tiers.Count` matches PitHouse rule.

### A single message can carry multiple "sections"

A *section* = sequence of TIER records terminated by an END_MARKER. A msg
may chain sections: `[ENABLE-list] TIER... END | [ENABLE-list] TIER... END`.

Examples:
- `112940` init 426B msg: 2 sections (flags 0,1 → END val=0; flags 2,3,4 → END val=16)
- `113616` 278B retransmit: 2 sections (flags 0,1,2 → END val=0; flags 3,4,5 → END val=9)
- `115840` 394B init: 2 sections (flags 0,1 → END val=0; flags 2,3,4 → END val=16)
- `113353` 442B observed: 2 sections (flags 0x11,0x12,0x13 → END=16; flags 0x14,0x15,0x16 → END=19)

The 89B "single-section" pattern in `113616` is the **earliest-possible
emission** that gets sent before the wheel has acked the section. The 278B
followup is the *cumulative* retransmit + section 2.

### Header-position ENABLEs reference *prior-section* tier flags

Within a section, `tag=0x00 size=1 ENABLE_PREV_TIER=N` records appear
**before** the section's TIER records. They carry the flag bytes of tiers
declared in earlier sections (or earlier messages) of the same session.

There are **no trailing per-tier ENABLEs** for newly-declared flags in the
current section. The act of declaring a TIER in a section is itself the
"enable" for that flag.

> Plugin already removed trailing per-tier ENABLEs. That matches captures.
> Re-adding them would break things.

### First tier-def of a session has special "no-enable" structure

The very first section of a session never has ENABLE records (nothing prior
to enable). It declares one or more tiers, ends with END_MARKER val=0.

When section 2 of the same first-message section follows, it ENABLEs flags
0..K-1 and declares K..M-1.

### The 89B + 278B pattern in 113616 is just chunked retransmit timing

89B at t+0.66 = first emission, single section, before wheel acked.
278B at t+3.77 = same prefix (proto+zero+tiers 0,1,2 + END=0) PLUS new
section (ENABLE 0,1,2 + tiers 3,4,5 + END=9).

This is **not** a deliberate PitHouse "two-shot" pattern. It is the same
single-message body, with a section appended after wheel acked the first.
Cumulative grows over time.

### Switching dashboards does NOT reset flag-base or re-declare prior tiers

When PitHouse switches dashboards mid-session (e.g. 115840 from Rally V3 to
Grids at t+~107s), the next tier-def emission:

1. Continues flag-base from where it left off (V3 ended at flag 0x07,
   Grids starts at 0x08).
2. ENABLEs 5,6,7 (last V3 flags).
3. Declares Grids tiers at flags 0x08,0x09,0x0a.
4. END val stays at 16 initially (no new channel idxes yet introduced).

A subsequent emission introduces fresh channel idxes (idx=17, 18, etc.)
when the new dash references catalog entries beyond the prior watermark.

### Channel records **only ever reference catalog indices**, never URLs inline

Across all 4 captures, **zero `tag=0x04` URL records observed**. The wheel
must already know URL→idx from earlier `b2h` catalog announcements. The V2
tier-def path is purely numeric.

> Plugin's V2 path can rely on `_wheelChannelCatalog` and never emit URLs.
> The V0 URL-based path is for older firmware only.

## Open questions (captures don't fully resolve)

### END_MARKER val semantics

Observed values, attempted hypotheses, and where they fail:

| Capture phase                  | END val | Channels in section | Max idx | Last flag |
|--------------------------------|---------|---------------------|---------|-----------|
| 112940 init secA (flags 0,1)   | 0       | 6                   | 6       | 1         |
| 112940 init secB (flags 2,3,4) | 16      | 16                  | 16      | 4         |
| 113616 89B (flags 0,1,2)       | 0       | 3                   | 3       | 2         |
| 113616 278B secB (flags 3,4,5) | 9       | 9                   | 9       | 5         |
| 113353 secA (flags 0x11..0x13) | 16      | 13                  | 16      | 0x13=19   |
| 113353 secB (flags 0x14..0x16) | 19      | 9                   | 9       | 0x16=22   |
| 115840 (flags 0x0b,0x0c)       | 18      | ~19                 | ≥18     | 0x0c=12   |
| 115840 (flags 0x0d,0x0e)       | 52      | ~20                 | ≥20     | 0x0e=14   |
| 115840 (flags 0x19,0x1a)       | 53      | 6                   | 6       | 0x1a=26   |
| 115840 (flags 0x1b,0x1c)       | 78      | 6                   | 6       | 0x1c=28   |

None of {channels-in-section, max-idx-in-section, last-flag,
total-channels-cumulative, channels-enabled} match all rows.

Best partial fits:
- "max idx in section" matches when it equals the channel count, fails when
  not (113353 secA: 13 channels but END=16; 115840 advanced phases).
- "max idx referenced anywhere in session up to this point" fits 112940
  and 113616 but cannot explain the 18 → 52 jump in 115840 (no idx >20
  visible in the partially-decoded N=12 tier).

> **Hypothesis to test (not from captures)**: END val may be a server-side
> "high-water-mark" that increments based on something other than the
> channel records visible in this section — possibly counting URL→idx
> additions on the wheel side (which arrive over a different stream we
> haven't decoded). Live test: send END val=16 on every section past init,
> see if wheel rejects.
>
> **Pragmatic plan**: have the negotiator emit END val = max channel idx
> ever referenced in any tier-def of this session so far. This matches
> three of four captures cleanly and the fourth (115840 advanced phases)
> we don't fully decode yet (TRUNC entries hide some channels in N=11/12
> tiers).

### What triggers section-2 emission

PitHouse emits section 2 (with ENABLE-of-section-1-flags) on a **timer
or ack signal** after section 1. Captures show:

- `113616`: 89B sent at t+0.66, 278B at t+3.77 — 3.1 s gap
- `112940`: full 2-section msg sent at t+0.5 (single shot)

Hypothesis: PitHouse waits for *something* (catalog ack? subscribe ack?)
between section-1 and section-2. Verifying requires correlating with `b2h`
traffic — out of scope for this Phase A pass; flag for Phase B.

### Cumulative redeclaration on retransmit

`115840` shows retransmits of older sections embedded in newer messages
(e.g. seq=64..79 contains section with flag 0x0b reused from earlier).
Whether the wheel needs the cumulative re-emit or accepts incremental is
unconfirmed. Plugin currently does single-emit; pre-switch test mode
works on that, so wheel does not strictly require cumulative.

## Implications for the Phase B state machine

1. **Single global flag counter** — never reset, never realign to round
   numbers. Increments by tier-count per emission. Maps cleanly to a
   single `_nextFlagBase` field.

2. **Initial vs switched dash converge** — both are "advance flag-base by
   prior-tier-count, ENABLE all prior flags in section header,
   declare new tiers, emit END val=watermark." There is **no separate
   code path needed** for switched-to vs initial.

3. **Preamble is a one-shot** at session start. State machine emits it on
   `WAIT_CATALOG → READY` transition once, never again until session reset.

4. **Sections are an emission boundary, not a wire-framing boundary** —
   one msg can hold N sections. The negotiator should produce a *list of
   sections*; chunker stitches them into one or more msgs.

5. **No URL records** in V2 emissions, ever.

6. **END val** — emit max-idx-ever-seen-in-session as best-known approximation.
   Open to revision if wheel rejects post-switch tier-defs and we discover
   the true rule.

## What this changes vs the existing code

- Drop `_priorSubscriptions` (full subscription history not needed —
  flag count per past dash is the only state required).
- Drop `_declaredFlags` set (replaced by single "highest flag emitted" int).
- Drop `Profile`-setter multi-broadcast expansion (PitHouse never replicates;
  we were synthesizing this as a heuristic to make pre-switch test mode work,
  but the captures show real PitHouse declares each tier exactly once).
  — **Verify** this doesn't regress pre-switch render before deleting.
- `BuildTierDefinitionMessageType02` collapses to ~30 lines: emit preamble
  if first-of-session, emit ENABLE for all prior flags, emit new tiers, emit
  END watermark.
