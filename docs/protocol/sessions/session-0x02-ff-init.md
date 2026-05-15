# Session 0x02 FF-record init handshake

PitHouse opens session 0x02 immediately after the management session and
sends a four-record FF-property init handshake. Without this handshake
the wheel silently ignores subsequent dashboard-switch (`kind=4`)
records, and the host's "switch" produces no visible change on the
display.

This doc is the canonical reference for the handshake protocol, the
inner FF-record envelope, and the known-broken shortcut of replaying
captured bytes verbatim.

## Outer wire format

Each FF-property record rides inside a normal SerialStream chunk on
session 0x02 (see [`chunk-format.md`](chunk-format.md) and
[`../wire/frame-format.md`](../wire/frame-format.md)). The chunk payload
is the FF-record envelope; multi-chunk records span multiple sequential
chunks and the wheel reassembles before parsing.

```
[0xFF]                    sentinel (1 byte)
[size:u32 LE]             length of kindAndValue (4 bytes)
[inner_crc:u32 LE]        zlib.crc32 of kindAndValue (4 bytes)
[kindAndValue]            size bytes:
    [kind:u32 LE]         record discriminator
    [payload]             kind-specific, size-4 bytes
```

`size` includes the 4-byte kind prefix; total wire bytes per record =
`size + 9`. The inner CRC covers the entire kindAndValue blob, not
just the payload. Builder is `Protocol/SessionPropertyPushBuilder.WrapFfRecord`.

## Init handshake (host → wheel)

Four FF records, in this order, on session 0x02 within ~150 ms of session
open. Plugin emits them from
[`Telemetry/TelemetrySender.SendSessionInitHandshake`](../../../Telemetry/TelemetrySender.cs).

| Kind | Name             | Wire size | Status in plugin (2026-05-13) |
|-----:|------------------|----------:|-------------------------------|
|    2 | `init_nonce`     |      16 B | Sent. Body partially decoded. |
|    7 | `init_enum`      |      12 B | Sent. Body partially decoded. |
|    8 | `init_catalog_a` |  ~1.7–2 KB| **NOT sent.** Decode incomplete; replay tested and locks wheel. |
|   11 | `init_catalog_b` |    ~2.5 KB| **NOT sent.** Decode incomplete; replay tested and locks wheel. |

## Wheel response (wheel → host)

~3.5 s after the host's full four-record burst, the wheel emits two FF
records on session 0x02 acknowledging the handshake:

| Kind | Name           | Wire size |
|-----:|----------------|----------:|
|   10 | `wheel_init_a` |      12 B |
|   16 | `wheel_init_b` |      20 B |

The ~3.5 s gap is consistent with the wheel decompressing and validating
the ~10 KB of inflated kind=8 + kind=11 catalogs against its internal
master tables. Until the host receives both kind=10 and kind=16, the
wheel will not echo `kind=4` dashboard-switch records and will not bind
post-switch tier-defs to display widgets.

## Why the host must send kind=8 / kind=11

`kind=2` (`init_nonce`) and `kind=7` (`init_enum`) alone are insufficient.
Cross-capture validation in `tools/bridge-decode-ff-init` against
`sim/logs/bridge-20260429-163951.jsonl` shows the wheel responds kind=10
+ kind=16 ONLY after all four records. The kind=8 and kind=11 records
carry zlib-compressed catalogs — kind=8 is a wheel-side dashboard slot
catalog (RpmAbsolute/RpmPercent slot names + wheel-internal property
names) and kind=11 is the FFB-property catalog (~250 increment/decrement
parameter names). See [`../findings/2026-05-07-sess02-ff-kinds-reference.md`](../findings/2026-05-07-sess02-ff-kinds-reference.md)
for the body decoding.

### Body decode — kind=8 and kind=11 record envelope (2026-05-15)

Both inflated catalogs use the **same record envelope**: a header, a
UTF-16-BE name, and a per-record TLV trailer whose `type` determines the
value layout:

```
record = [id:u16 BE] [name_len:u32 BE] [UTF-16BE name (name_len bytes)] [TLV trailer]
TLV trailer = [type:u32 BE] [reserved:u8] [value: type-determined layout]
```

| `type` | Trailer total | Value layout | Where seen |
|------:|--------------:|--------------|------------|
| 0  | 5 B  | (no value; reserved byte = `0x01`) | RpmAbsolute / RpmPercent slot records (kind=8) |
| 2  | 9 B  | `[u32 BE]` | enum / index / bool wheel settings (kind=8) |
| 4  | 13 B | `[u64 BE]` (integer property, e.g. brightness=80, breath interval=3000 ms) | kind=8 |
| 6  | 13 B | `[u64 BE]` interpreted as `double BE` (e.g. `equalizerGain1 = 90.0`, `naturalDamper = 42.0`) | kind=8 |
| 10 | 9 + strlen B | `[strlen:u32 BE] [UTF-16BE string]` (e.g. `__location → "en_US"`) | kind=8 |
| 9  | variable | nested preset block (UTF-16-LE-encoded inner sub-records, partially decoded) | kind=8 |

`kind=11` uses the same envelope but only emits records with the
simplest trailer shape (`type=0`, name-only), one per FFB property name
(`decrementEqualizerGain1`, …). `kind=8` emits the full mix.

Full byte-exact examples and the type=9 inner-block partial decode live
in [`../findings/2026-05-15-sess02-kind8-tlv-and-preset-block.md`](../findings/2026-05-15-sess02-kind8-tlv-and-preset-block.md).

## **Do not replay captured kind=8 / kind=11 bytes verbatim**

The kind=8 and kind=11 records from a PitHouse capture cannot be shipped
back to the wheel as a static snapshot — the wheel locks. This was
verified destructively on a W17 (CS Pro) wheel on 2026-05-13 and the
binary captures have been deleted from the repo precisely so a future
agent can't grab them as a shortcut.

**Verified failure, 2026-05-13:** the plugin briefly wired
`SendSessionInitHandshake` to ship verbatim kind=8 (2059 B) and kind=11
(2581 B) bytes extracted from `sim/logs/bridge-20260429-163951.jsonl` on
every `StartInner` cycle. With a W17 / CS Pro wheel:

- First emission: handshake completed, wheel began echoing `kind=4`
  switches, dashboard switching worked for ~5 minutes.
- Second/third emission across dashboard-switch restart cycles: wheel
  locked into a state where it stopped responding to any further
  commands and required a physical power-cycle to recover.

Diagnostic bundle: `~/CS-Pro-moza-diagnostics-bundle-20260513-122621.zip`
captured the lock-up. The replay path was reverted in the same session
and the source `.bin` files were deleted (previously at
`Resources/sess02_init_kind{8,11}_pithouse.bin`) so they can't be
re-embedded thoughtlessly. Re-derive byte content from
`sim/logs/bridge-20260429-163951.jsonl` via
`tools/bridge-decode-ff-init` for decode work; do NOT ship them.

The records carry session-bound state that becomes invalid when replayed:

- `kind=2` body offsets 0..3 are a fresh Unix timestamp (the plugin
  already regenerates this — see
  [`../findings/2026-05-07-sess02-ff-kinds-reference.md`](../findings/2026-05-07-sess02-ff-kinds-reference.md)
  body-decode section), but offsets 12..15 vary per capture and are
  almost certainly a derived value (CRC of the timestamp+magic, or a
  session salt the wheel verifies).
- `kind=8` size grows monotonically within a single PitHouse session
  (1740, 1769, 1780, …, 2050 B observed in one capture) which strongly
  suggests it encodes session-cumulative state, not a static blob.
- `kind=11` similarly trends with session activity.

The wheel apparently validates these against state it built up in
parallel during the same session; presenting it with a snapshot from
a different session at handshake re-emission desynchronises that state
beyond what the wheel's input handler can recover from cleanly.

## Required work before re-enabling kind=8 / kind=11 emission

1. ~~Decode the inner structure of kind=8 sub-format B (the wheel-config
   property records — sub-format A is already understood). Identify
   which fields are wheel-static (constants the host can replay) vs
   session-derived.~~ **Done 2026-05-15** for trailer types 0/2/4/6/10
   — see the TLV trailer table above and
   [`../findings/2026-05-15-sess02-kind8-tlv-and-preset-block.md`](../findings/2026-05-15-sess02-kind8-tlv-and-preset-block.md).
   Type=9 (nested preset blocks) is partially decoded — outer envelope
   known, inner field semantics still TBD. Most type=4/6 values are
   user-settable wheel settings (brightness, EQ gains, intervals); these
   are session-derived in the sense that they reflect user state at
   record-emission time, not in the wheel-validates-cryptographic-token
   sense.
2. Decode `kind=11` body record boundaries (currently parsed as a flat
   `[id:u32 BE][name_len:u32 BE][name:UTF-16-BE]` stream — verify there
   are no embedded sub-fields or session-derived values).
3. Decode `kind=2` body offsets 12..15. Compare against a CRC32 of the
   leading 12 bytes; if not a CRC, treat as a salt and search for the
   generator (e.g. wheel-side nonce echoed in `wheel_init_a`/`b`).
4. Re-derive whether the wheel actually requires the FULL kind=8 / kind=11
   payloads, or whether a minimal valid record (empty list, or only the
   records the host has authority over) is sufficient.
5. Build `BuildSessionInitField8Body` / `BuildSessionInitField11Body` in
   `Protocol/SessionPropertyPushBuilder` that mint per-session-correct
   records. Then emit from `SendSessionInitHandshake` ONCE per cold
   start, gated against re-emission across restart cycles unless the
   wheel itself invalidates session state (e.g. across a sess=0x02
   close+reopen).

Only after all four are answered can `SendSessionInitHandshake` resume
shipping kind=8 / kind=11. The kind=2 / kind=7 records continue to be
emitted; that is necessary but not sufficient to engage dashboard
switching, and the user-visible consequence of insufficient-handshake is
documented (kind=4 silently ignored) but at least non-destructive.

## Related docs

- Body decoding details and capture-by-capture diff:
  [`../findings/2026-05-07-sess02-ff-kinds-reference.md`](../findings/2026-05-07-sess02-ff-kinds-reference.md)
- Why kind=4 needs the full handshake (with wire-trace evidence):
  [`../findings/2026-05-07-sess02-init-protocol-and-stale-catalog.md`](../findings/2026-05-07-sess02-init-protocol-and-stale-catalog.md)
- Inner FF-record envelope detail:
  [`../findings/2026-04-29-session-01-property-push.md`](../findings/2026-04-29-session-01-property-push.md)
- `kind=4` dashboard switch wire signal:
  [`../findings/2026-04-30-dashboard-switch-3f27.md`](../findings/2026-04-30-dashboard-switch-3f27.md)

## Decode tools

- `tools/bridge-decode-ff-init <capture>` — reassembles multi-chunk FF
  records from a PitHouse bridge JSONL and (where applicable) zlib-
  decompresses the body. Use this to validate body changes per
  firmware version before reimplementing the host emit.
- `tools/trace-sess02-decode <wire-trace>` — same shape against our
  own wire-trace JSONL. `h2b sess=0x02: no chunks` (or "kind=2/7 only")
  is the signature that the plugin's emit path is incomplete.
