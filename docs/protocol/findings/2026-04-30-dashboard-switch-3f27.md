# Dashboard switch wire signals (session 0x02 FF-record + `0x3F 27:NN`)

**Date:** 2026-04-30
**Capture:** `wireshark/csp/startup, change knob colors, change dash several times, delete dash.pcapng`
**Hardware:** CSP firmware (R5 base + W17 wheel) — Pithouse 1.2.6.17
**Status:** UNTESTED in plugin. Wire format verified from capture; behaviour against live wheel from plugin not confirmed.

## Summary

Two distinct host→wheel signals observed during user-driven dashboard switches in the same capture. Roles inferred but not yet validated by replay testing:

1. **FF-record on session 0x02** — likely the **primary** "set active dashboard from list" command (Dashboard Manager click).
2. **`0x3F 0x17 27:[page]` direct frame** — likely a **secondary** per-page fingerprint update / state echo.

Both formats are documented below. Plugin should try the FF-record path first.

---

## Mechanism 1 — FF-record on session 0x02 (primary candidate)

External writeup of this signal arrived 2026-04-30 from RE of capture
`automobilista-switch-dashboard-many-ends-on-grids-1.2.6.17.pcapng`. Wire
format described there matches records found in our capture too.

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
  [field2:LE32]        // 1-based slot index into wheel's enabled-dashboards list
  [field3:LE32]        // = 0
  [body_crc:LE32]      // CRC32 of (ff || data_size || data_crc || field1 || field2 || field3)
```

CRC32 = standard polynomial `0xEDB88320`, init `0xFFFFFFFF`, XOR-out
`0xFFFFFFFF` (Python `zlib.crc32` / Java `java.util.zip.CRC32`).

### Captured records (this repo's capture)

```
t=47.89s   fn=97411   field1=7  field2=3   (startup — different cmd, NOT a switch)
t=212.10s  fn=558353  field1=4  field2=0
t=214.91s  fn=566115  field1=4  field2=1
t=223.13s  fn=588199  field1=4  field2=0
t=225.14s  fn=593671  field1=4  field2=2
t=226.79s  fn=598193  field1=4  field2=3
t=228.55s  fn=603069  field1=4  field2=4
t=238.74s  fn=631645  field1=4  field2=10
```

`field1=4` records: 7 distinct slot writes, slots 0..4 + 10. Sequential
cycling pattern matches user clicking through Dashboard Manager entries.

`field1=7` at startup likely a different opcode (init / version
declaration); not used for switching.

### Slot index source

Slot = 1-based position in the wheel's enabled-dashboards list. List comes
from the session 0x09 configJson state push (`enableManager.dashboards`
array). Slot 1 = first entry, slot N = N'th entry.

### Wheel response

After the FF-record:
1. Wheel FC-acks the chunk on page 0x02
2. Wheel echoes the record back on page 0x02 dev→host
3. Wheel re-pushes binding catalog on page 0x01 (TLV records `00 01`,
   `01 xx`, `03`, `06 04`, `0b`)

External writeup states: no session re-open, no LVGL re-upload, no tier-def
re-broadcast required.

### Implementation skeleton (C# for plugin)

```csharp
byte[] BuildSwitchRecord(uint slotIndex)
{
    // 12-byte data block
    Span<byte> data = stackalloc byte[12];
    BinaryPrimitives.WriteUInt32LittleEndian(data[0..4], 4u);          // field1
    BinaryPrimitives.WriteUInt32LittleEndian(data[4..8], slotIndex);   // field2
    BinaryPrimitives.WriteUInt32LittleEndian(data[8..12], 0u);         // field3

    uint dataCrc = Crc32.Compute(data);

    // 21-byte body
    Span<byte> body = stackalloc byte[21];
    body[0] = 0xff;
    BinaryPrimitives.WriteUInt32LittleEndian(body[1..5], 12u);         // data_size
    BinaryPrimitives.WriteUInt32LittleEndian(body[5..9], dataCrc);
    data.CopyTo(body[9..21]);

    uint bodyCrc = Crc32.Compute(body);

    // 25-byte payload = body + bodyCrc
    var payload = new byte[25];
    body.CopyTo(payload);
    BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(21), bodyCrc);
    return payload;
}
```

Wrap with the SerialStream chunk header `7c 00 02 01 [seq:LE16]` before
sending — same path used for tier-def / property-push on session 0x02.

---

## Mechanism 2 — `0x3F 0x17 27:[page]` direct write (secondary / TBD role)

Originally suspected as the primary switch path; later evidence suggests
it's a per-page state update that may run alongside (or downstream of) the
FF-record above. Documented for completeness but **not** the recommended
plugin-driven switch path.

### Wire format

```
write : 7e 06 3f 17 27 [page] [flag:1] [fingerprint:3] [csum]
read  : 7e 03 40 17 27 [page] 00
reply : 7e 06 c0 71 27 [page] [flag:1] [fingerprint:3] [csum]
```

`page` ∈ {0, 1, 2, 3}. `flag` byte: `0x00` = primary state, `0x01` =
alternate state (semantics TBD). `fingerprint` = 24-bit opaque ID.

### Captured writes

```
t=49.57s  page 3 ← 00 f6 b4 99   (initial bind at startup)
t=49.59s  page 1 ← 00 c6 e1 9b
t=49.60s  page 0 ← 00 f3 79 a1
t=49.61s  page 2 ← 00 9b a7 eb
t=49.88s  page 2 ← 01 11 47 e6
t=150.57s page 2 ← 00 ff 64 00   ← user-driven event
t=166.78s page 3 ← 00 5f 97 ff   ← user-driven event
```

Note these timestamps are DIFFERENT from the FF-record timestamps
(t=212–238s) — same capture, different events. Two distinct user actions
exercise two distinct mechanisms.

### Fingerprint origin

Opaque, NOT derivable from any visible field:
- Doesn't match dashboard `id` / `dirName` / `title` / `hash` from session 0x09
- Doesn't match MD5/SHA1/SHA256/CRC32 of any of those
- Doesn't match MD5/SHA1/SHA256/CRC32 of mzdash file bytes

Likely wheel-assigned at upload commit; or computed by a custom hash
function we haven't reversed.

---

## Plugin recommendation

**Primary:** implement the FF-record path. Inputs needed:
- Slot index (1-based) from session 0x09 `enableManager.dashboards` order
- CRC32 (zlib-compatible)
- SerialStream sequence counter for page 0x02

**Verification:** after sending, confirm wheel:
- FC-acks the chunk
- Echoes record back on page 0x02 dev→host
- Re-pushes binding catalog on page 0x01

If either signal is missing or wheel ignores the record, fall back to
investigating the `0x3F 27:NN` path with a harvested fingerprint table.

**Both paths are UNTESTED from the plugin side.** Do not treat either as
production-ready until live wheel replay confirms behaviour.

---

## Doc cross-refs

- [`../channel-config/group-0x40-burst.md`](../channel-config/group-0x40-burst.md) § 27:NN dashboard-switch — `3F 27:NN` wire format details
- [`../../usb-capture/payload-09-state-re.md`](../../../usb-capture/payload-09-state-re.md) — earlier search history; FF-record format was the actually-used signal, not `3F:28`
