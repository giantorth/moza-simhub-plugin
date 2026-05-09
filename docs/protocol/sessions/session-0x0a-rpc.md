### Session `0x0a` RPC (host → device)

Session `0x0a` carries JSON-RPC requests from PitHouse to the wheel and the
matching replies from the wheel back to PitHouse. The transport is the same
9-byte compressed envelope used by `configJson` on session `0x09` (see
[`compressed-0x09-0x0a.md`](compressed-0x09-0x0a.md)).

### Wire envelope

Reassembled message body (after stripping per-chunk CRC32-LE trailers):

```
flags(1) = 0x00
comp_sz+4(4 LE)
uncomp_sz(4 LE)
zlib_stream(...)
```

The decompressed payload is a single UTF-8 JSON object.

### Request shape

```json
{"<method>()": <arg>, "id": <N>}
```

- The method name is **suffixed with `()`** in the key string — literal
  parentheses are part of the JSON key, not function-call syntax.
- `<arg>` is the method-specific argument (string, object, or empty string).
- `<N>` is the RPC id (see "id semantics" below).

### Reply shape

**Reply mirrors the request key.** Plugin sends:

```json
{"<method>()": <return>, "id": <same N>}
```

NOT `{"id": N, "result": ...}`. Earlier sim revisions emitted the
`{id,result}` form; PitHouse silently dropped those, leaving the Dashboard
Manager stuck on the pre-call state. Switching to mirrored-key replies
cleared the stall (verified 2026-04-22 with the delete RPC). For methods
where no real wheel reply has been captured (e.g. `completelyRemove`),
plugin uses an empty string `""` for `<return>`.

### Known methods

Observed in PitHouse capture, 2026-04-21:

| UI action | Method | Arg | Notes |
|-----------|--------|-----|-------|
| Delete dashboard | `completelyRemove()` | `"{<uuid>}"` | UUID in Microsoft GUID brace form, e.g. `{7c218515-6ec6-4e5f-9820-ba030b14c43d}`. **Not the id from `enableManager.dashboards[].id`** — it is PitHouse's per-install cache key. Sim falls back to `dirName` / `hash` / `title` matching, plus a single-non-factory-dashboard heuristic. UUIDs may also be all-zero placeholders (`{00000000-0000-0000-0000-000000000003}`) or 32-char random strings (`gLib1v4iWa5XZBCDew8R71yImlYyyaBC`). |
| Reset dashboard | `()` (empty method name) | `""` | Literal empty parentheses as the key |

### `id` semantics

`id` is **NOT** a monotonic RPC counter. PitHouse-sim observation
(2026-04-21):

- Four rapid consecutive "Reset Dashboard" clicks within one connect → all
  four frames carried `id=13`.
- A prior connect used `id=15` for a single reset.

`id` appears to be a **session-scoped target reference** assigned at
connect, reused for every call against the same UI item. Different connect
→ different id. Practical implication for the wheel side: accept any
integer id and echo it back in the reply. Do not assume sequential ids,
do not validate ranges.

### Plugin entry point

[`TelemetrySender.SendRpcCall(method, arg, timeoutMs)`](../../../Telemetry/TelemetrySender.cs)
posts the request and returns a `Task<JsonElement?>` that completes when
the wheel's reply arrives. Replies are routed to waiting tasks by `id`
through a dictionary so multiple in-flight RPCs are tracked concurrently.

### Worked example: reset

Request, after envelope decode:

```json
{"()": "", "id": 13}
```

On-wire (host → device, 17-byte JSON body, 25-byte zlib stream, 9-byte
envelope, then chunked over `7c:00` on session `0x0a`):

```
00              flags
1d 00 00 00     comp_sz+4 = 29
11 00 00 00     uncomp_sz = 17
78 9c …         zlib(JSON)
```

Reply (device → host):

```json
{"()": "", "id": 13}
```

Same envelope, same id.
