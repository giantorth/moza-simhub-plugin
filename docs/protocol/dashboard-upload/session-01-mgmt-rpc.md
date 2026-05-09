### Session `0x01` management RPC envelope

Session `0x01` carries management traffic — wheel identity reads, debug log
push, channel catalog binary (KS Pro) — wrapped in an `0xFF`-prefixed
sub-message envelope distinct from the `0x09`/`0x0a` 9-byte compressed
envelope.

> **2026-04 firmware path.** Wheels: VGS, CSP. Capture: `09-04-26/dash-upload.pcapng`.
> See [`../FIRMWARE.md`](../FIRMWARE.md) for the firmware-era matrix. Note
> [`path-a-session-01-ff.md`](path-a-session-01-ff.md) describes the
> *legacy dashboard upload* path that also rides session 0x01 with this
> envelope; the layout below is the **management** form, not the upload form.

### Wire envelope

```
FF(1)  inner_len(4 LE)  token(4 LE)  body(inner_len)  CRC32(4 LE)
```

| Offset | Size | Field | Notes |
|--------|------|-------|-------|
| 0 | 1 | `0xFF` | Sub-msg sentinel |
| 1 | 4 | inner_len (LE u32) | Body byte count (excludes header, token, and CRC) |
| 5 | 4 | token (LE u32) | Correlation ID linking request to response — same on both directions |
| 9 | inner_len | body | Application data (zlib log, channel catalog, etc.) |
| 9+inner_len | 4 | CRC32-LE | CRC over **all preceding bytes** from `FF` through end of body |

The CRC covers the sentinel, length, token, and body — not just the body.
This is the same coverage used by the legacy session-01 dashboard upload
path (see [`path-a-session-01-ff.md`](path-a-session-01-ff.md)) — both
share envelope structure.

### Token semantics

Token links request to response: when host issues a management read, the
wheel's reply on session 0x01 carries the same 4-byte token. This is how
the host disambiguates concurrent reads with the same group/cmd. Token
generation is host-side; firmware echoes verbatim. Random `u32` is
sufficient.

### Per-chunk CRC and reassembly

Multi-chunk messages also get the **per-chunk** CRC32-LE trailer added by
the SerialStream layer (see [`../sessions/chunk-format.md`](../sessions/chunk-format.md)).
So the on-wire path for a multi-chunk management RPC is:

```
[Per-chunk CRC] is added to each 7c:00 data chunk's net body
[Message CRC32] sits at the end of the assembled body, after `body`
```

Reassembler must:

1. Strip per-chunk CRC32-LE from each chunk's net payload.
2. Concatenate trimmed payloads in seq order.
3. Read FF-prefix envelope: parse `inner_len`, `token`, `body`,
   message-CRC.
4. Validate message-CRC over `[FF .. body]`.
5. Hand `body` to the consumer (zlib decoder for log, binary parser for
   catalog, etc.).

### Observed bodies

| Capture | Direction | Inner len | Body |
|---------|-----------|-----------|------|
| `moza-startup.pcapng` (VGS, t=5.2s) | dev → host | 7163 | zlib-compressed UTF-16BE device log: installed dashboards + render status |
| KS Pro `putOnWheelAndOpenPitHouse.pcapng` | host → dev | varies | binary channel catalog push |

Decompressed device log sample (UTF-16BE → UTF-8):

```
[INFO] Active dashboard: rpm-only
[INFO] Loaded dashboards: rpm-only, Formula 1, GT V01, ...
[INFO] Display ready
```

### Distinct from session-01 dashboard upload

The legacy 2026-04 firmware also uses session 0x01 to upload `.mzdash`
files via three sequential FF-prefixed sub-messages with the same envelope
structure. See [`path-a-session-01-ff.md`](path-a-session-01-ff.md) for
the upload-specific layout. The two coexist on session 0x01 — distinguish
by inspecting the body: management RPCs carry zlib log / catalog content;
upload sub-messages carry one of the three field types (tokens / protocol
constant / mzdash content).
