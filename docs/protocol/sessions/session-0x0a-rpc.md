### Session 0x0a RPC (host → device)

Plugin exposes `TelemetrySender.SendRpcCall(method, arg, timeoutMs)` to send JSON RPCs on session 0x0a in same 9-byte `[flag=0x00][comp_size+4:u32 LE][uncomp_size:u32 LE][zlib]` envelope used by `configJson` on session 0x09. Request shape `{"<method>()": <arg>, "id": <N>}`. Reply shape **mirrors the request**: `{"<method>()": <return>, "id": <same N>}` — NOT `{"id": N, "result": ...}`. The `<return>` value is an empty string for the reset RPC (only shape confirmed by pcap); sim uses `""` for every reply pending a capture of a real wheel's `completelyRemove` reply. Replies routed by `id` via dictionary of waiters so multiple in-flight RPCs tracked concurrently.

**Cross-check (2026-04-22):** earlier sim emitted `{"id": N, "result": ...}` replies and PitHouse silently dropped them — the Dashboard Manager stayed stuck on the pre-delete state and refused to initiate any subsequent upload. Switching to the mirrored-key reply shape cleared the stall on the sim side (delete RPC round-trip confirmed end-to-end).

Known methods (observed via pithouse sim capture, 2026-04-21):

| UI action | Request | Notes |
|-----------|---------|-------|
| Delete dashboard | `{"completelyRemove()": "{<uuid>}", "id": N}` | UUID in Microsoft GUID format e.g. `{7c218515-6ec6-4e5f-9820-ba030b14c43d}`. **The `<uuid>` is PitHouse's own per-install cache key, NOT the id the sim advertised in `enableManager.dashboards[].id`.** Observed uuids include all-zero placeholders like `{00000000-0000-0000-0000-000000000003}` and random 32-char strings (`gLib1v4iWa5XZBCDew8R71yImlYyyaBC`). Sim-side delete handlers must fall back to dirName/hash/title matching (and a single-non-factory-dashboard heuristic when FS holds exactly one user upload) because the uuid will never match whatever the sim reports on session 0x09. |
| Reset dashboard | `{"()": "", "id": N}` | **Empty method name** (literal `()`), empty args |

**Observed id semantics (PitHouse sim, 2026-04-21):** id is **NOT a monotonic RPC counter**. Across 4 rapid consecutive "Reset Dashboard" clicks within one Pithouse session, all 4 frames carried `id=13`. A prior Pithouse session used `id=15` for a single reset. Id appears to be a **session-scoped target reference** — assigned by Pithouse at connect time, reused for all calls targeting the same item. Different connect = different id. Practical implication: wheel sim / plugin should accept any integer id and echo it back in reply, not expect sequential ids.
