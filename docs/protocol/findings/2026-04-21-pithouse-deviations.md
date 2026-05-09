## PitHouse-observed deviations (2026-04-21 sim captures)

Recorded during live PitHouse ↔ wheel_sim testing. Each item documents a behaviour seen on wire that deviates from, refines, or extends claims elsewhere in this doc.

> **Canonical topical homes for facts in this journal:**
>
> | H3 section | Now also documented at |
> |------------|------------------------|
> | configJsonList is NOT factory-canonical | [`../dashboard-upload/config-rpc-session-09.md`](../dashboard-upload/config-rpc-session-09.md) |
> | Session 0x0a RPC id is target-scoped, not counter | [`../sessions/session-0x0a-rpc.md`](../sessions/session-0x0a-rpc.md) |
> | Display sub-device identity randomisation required | [`../identity/display-sub-device.md`](../identity/display-sub-device.md) |
> | Session 0x04 root dir listing: persistent paths | [`../dashboard-upload/session-04-root-dir.md`](../dashboard-upload/session-04-root-dir.md) |
> | Canonical RPC method envelope variants | [`../sessions/session-0x0a-rpc.md`](../sessions/session-0x0a-rpc.md) |
> | Session 0x03 is host→wheel ONLY | [`../sessions/session-0x03-tile-envelope.md`](../sessions/session-0x03-tile-envelope.md) |
> | Dashboard upload traffic missing when wheel has dashboard | [`../dashboard-upload/README.md`](../dashboard-upload/README.md) |
> | Cold-start: PitHouse skips tier_def push on reconnect | [`../tier-definition/handshake.md`](../tier-definition/handshake.md) |
> | configJson state push includes top-level fields many docs omit | [`../dashboard-upload/config-rpc-session-09.md`](../dashboard-upload/config-rpc-session-09.md) |
>
> Other H3s (`completelyRemove` arg mismatch, PitHouse cache-skip, no dictionary re-push) are journal-only — capture-specific bug observations.

### configJsonList is NOT factory-canonical

Doc § 857 shows an 11-name list (`Core, Grids, Mono, Nebula, Pulse, Rally V1..V6`) extracted from `automobilista2-wheel-connect-dash-change.pcapng`. These are NOT factory-canonical dashboards shipped by MOZA — they are the user's already-installed dashboards for that wheel. Different captures from different wheels will show different lists.

**Practical consequence:** `configJsonList` derives from current wheel-installed dashboard directory names, not from a fixed firmware catalog. A factory-fresh wheel's `configJsonList` is likely empty or shorter than the observed 11. Sim implementations should derive the list from `enableManager.dashboards[].dirName` rather than hardcoding names — `build_configjson_state()` in `sim/wheel_sim.py` does exactly this (2026-04-22 change).

**Empty-list behaviour confirmed (2026-04-22):** re-tested on Windows PitHouse + real USB CDC gadget. Emitting `configJsonList=[]` with every other top-level field populated kept PitHouse at `sessions_opened=0`, `tier_def_received=false` indefinitely. Restoring a non-empty list (the factory 11-name placeholder as a safety fallback for empty FS) brought the handshake back within a single reconnect. **Rule: keep at least one entry in `configJsonList` at all times.** Sim currently falls back to the factory 11-name list when FS is empty; once the exact gating condition is reverse-engineered from firmware, the fallback can be tightened (a single placeholder name may suffice).

### Session 0x0a RPC id is target-scoped, not counter

Doc § 663 previously implied sequential id assignment. Captures of 4 consecutive "Reset Dashboard" clicks in one PitHouse session all carried identical `id=13`; a separate earlier session used `id=15` for a different click. Id is a session-scoped target reference assigned once by PitHouse per item, reused across every RPC call targeting that item.

### `completelyRemove` arg does not match sim-advertised ids (2026-04-22)

Testing with Windows PitHouse against the USB-CDC gadget sim confirmed that the `<uuid>` PitHouse sends in `completelyRemove()` is **never** the id the sim advertised in its most recent `enableManager.dashboards[].id` push. Observed uuids across five delete clicks:

- `gLib1v4iWa5XZBCDew8R71yImlYyyaBC` — 32-char random string (factory-id format)
- `{b6fd8a33-8b10-4c32-8451-7e97c6073f83}` — random Microsoft GUID
- `{00000000-0000-0000-0000-000000000002}`, `{…000000000003}`, `{…000000000004}` — all-zero placeholders with varying last byte
- `{177b97ff-43f4-4fa3-bc27-9db20449c165}` — another random GUID
- `{2e869528-6e4e-4d08-a4cb-0c3981c42df0}` — another random GUID

Sim's synthetic id format (`sim-<md5[:8]>-<dirName>`) never appeared. PitHouse draws the uuid from its own per-install cache rather than echoing whatever `enableManager.dashboards[].id` the wheel most recently reported. This matches the "PitHouse local cache" hypothesis from § PitHouse cache-skip prevents upload.

**Sim-side practical handling** (implemented in `_handle_rpc` / `completelyRemove` branch of `sim/wheel_sim.py`):

1. Try exact match on every FS-derived `id`, `dirName`, `hash`, `title`.
2. Try a `_pithouse_dashboard_ids[arg]` lookup populated opportunistically from any `configJson()` host→wheel reply the sim observes.
3. Try matching against the captured factory `enableManager.dashboards[].id` list (for factory-preset dashboards).
4. **Last-resort fallback:** if FS contains exactly one non-factory dashboard, delete it anyway — PitHouse's UI is the ground truth for "user wants this deleted" and the sim has no other way to resolve the uuid.
5. **Always fire `_fire_state_refresh()` after handling** (even on no-op) so PitHouse's Dashboard Manager re-syncs against the current wheel state. Without the refresh, PitHouse caches a stale view and will re-issue the same delete on the next UI interaction.

Reply on session 0x0a uses the mirrored-key shape `{"completelyRemove()": "", "id": <same N>}` with the 9-byte envelope (§ Compressed transfer format). Sim's earlier `{"id": N, "result": {...}}` shape was silently dropped by PitHouse.

### PitHouse cache-skip prevents upload RPC under some condition we can't bypass from sim

Observed: clicking "Upload Dashboard" on a brand-new dashboard "horse" / "lol" / "test" produced ZERO host→wheel traffic on session 0x04 across multiple experiments, even with:
- Fresh sim filesystem (0 files, `enableManager.dashboards=[]`)
- Randomised `hw_id` / `serial0` per sim start (see `_apply_model` in `sim/mcp_server.py`)
- Randomised Display sub-device `hw_id` / `serial0` per sim start
- Empty `/home/moza/resource/dashes/` in session 0x04 root-dir listing
- Full 11-field configJson state schema matching real capture

PitHouse appears to cache "dashboard X already uploaded to wheel Y" entirely PC-side — keyed by something we could not identify (possibly a hash of wheel serials that doesn't match our randomised versions, or a persistent local DB at `%APPDATA%\MOZA Pit House\`). The `mcUid` mechanism documented at § 702 (`MainMcuUidCommand`) is the most likely cache key but its wire format remains unreversed (see `usb-capture/main-mcu-uid-re.md`).

Workaround to force upload: wipe PitHouse local data (`drive_c/users/steamuser/AppData/Local/MOZA Pit House/` + `Documents/MOZA Pit House/`) while PitHouse is closed.

### Display sub-device identity randomisation required, not just wheel identity

Plugin-side `_apply_model` in `sim/mcp_server.py` randomises both `model['hw_id']` AND `model['display']['hw_id']` on every sim start because PitHouse probes the display sub-device independently via 0x43 → dev 0x17. Random wheel identity alone is insufficient — dashboard management operations key on display identity, not base wheel identity.

### Session 0x04 root dir listing: persistent paths expected

Doc § 823 shows listing with `{"name":"temp"}` under root. In practice, a factory-fresh wheel keeps `/home/moza/resource/dashes/` as a persistent directory path even with no dashboards installed. PitHouse needs this path present in the listing to know where uploads should land. Sim `_synthesize_empty_fs_skeleton()` returns the 5-level `root/home/moza/resource/dashes` tree when FS is empty.

### Pithouse does not re-push dictionary blobs on reconnect

Doc § 1220 claims PitHouse uploads 5 zlib blobs (session 0x02 channel-name + action-name dictionaries, session 0x03 tile-server) on every connect. Observed: on sim_reload without Pithouse cycling, blobs did NOT re-transmit — Pithouse dedupes by its own state tracking. Only a fresh PitHouse connection (after wheel re-enumerate or PitHouse restart) triggers the full 3-blob push.

### Canonical RPC method envelope variants

Doc § 663 says session 0x0a uses the same 9-byte `[flag=0x00][comp_size+4 LE][uncomp_size LE]` envelope as session 0x09. Verified. But **session 0x04 root-directory listing** uses a 53-byte prefix (tag `0x0a` + size + UTF-16LE path + padding + metadata; see § Session 0x04 device → host root directory listing) and **session 0x03 tile-server** uses a **12-byte** envelope with `FF 01 00 ... FF 00 ...` + u24 BE uncompressed size — documented in § Session 0x03 tile-server envelope above. Same zlib body, different wrapper on every session. Do not assume one envelope shape fits all zlib-carrying sessions.

### Session 0x03 is host→wheel ONLY (verified 2026-04-22)

Scanned 5 captures for device→host traffic on session 0x03:

| Capture | Host→device | Device→host |
|---------|------------|-------------|
| `automobilista2-wheel-connect-dash-change.pcapng` | 82 | **0** |
| `automobilista2-dash-change.pcapng` | 17 | **0** |
| `connect-wheel-start-game.pcapng` | 90 | **0** |
| `12-04-26/moza-startup.pcapng` | 80 | **0** |
| `09-04-26/dash-upload.pcapng` | 1 | **0** |

**Wheel never pushes on session 0x03.** Session is one-way for PitHouse → wheel tile-server state uploads. Plugin's session 0x03 inbound parser (`TileServerStateParser`) stays dormant in real-wheel operation — kept for future firmware behaviour changes but no capture-driven requirement to exercise.

### Dashboard upload traffic missing when PitHouse thinks wheel has dashboard

Even with `enableManager.dashboards=[]` and filesystem listing showing empty `/home/moza/resource/dashes/`, PitHouse's UI sometimes displays dashboards as "already on device" and suppresses the upload RPC entirely. Indicates PitHouse keeps its own "what I last pushed to this wheel" record separate from what the wheel's session 0x09 state reports. Re-sync to empty requires either clearing PitHouse local cache OR presenting an entirely new wheel identity.

### Cold-start: PitHouse skips tier_def push on reconnect (2026-04-24)

When PitHouse is relaunched while the wheel is already enumerated (sim stayed up across PitHouse restart), PitHouse re-opens sessions 0x01/0x02/0x03 BUT sends only `00 00 00 00` keepalives on 0x01 — no tier_def push, no channel catalog ack. Without the tier_def cycle, PitHouse never sends `display_cfg` (`7c:23 46`) and therefore never asks the wheel to open dynamic upload sessions. Result: UI shows wheel connected but uploads silently no-op.

Mechanism: PitHouse caches per-wheel-identity state across its own process lifecycle. On reconnect it short-circuits the tier_def negotiation. The trigger that re-arms PitHouse's push is the **device-side channel catalog** (sessions 0x01 + 0x02 wheel→host frames sent during initial handshake). The proactive_sender thread in `wheel_sim.py` only emits this catalog ONCE at sim startup, so PitHouse reconnect on a long-running sim sees no new catalog → cache stays sticky → no tier_def.

Fix (sim, 2026-04-24): `_fire_device_init` now re-emits the channel catalog frames on every handshake — startup AND reconnect (via `_reset_connection_state` clearing `_device_init_started`). `proactive_sender` skips its one-shot catalog emit if `catalog_sent` is already True, avoiding duplicate sends. After this fix PitHouse pushes a fresh tier_def on every reconnect, display_cfg flows, uploads work.

The user's prior workaround was `sim_reload` + `sim_start` after every PitHouse restart — Windows sees the underlying USB gadget drop and reattach, which forces PitHouse to re-enumerate and clear its cache. With the catalog-re-emit fix, that workaround is no longer required.

**Sim-restart resume: plugin recent commit (2026-04-26 `567ed25`) only closes host-managed sessions (0x01..0x03) on connect; device-managed sessions (0x04..0x10) stay alive across SimHub restart.** Combined with sim_restart, this means plugin can resume existing 0x01/0x02/0x03 sessions WITHOUT sending fresh `7c:00 [sess] 81` OPEN frames — just data chunks. Original `_fire_device_init` was gated on `sessions_opened >= 2` from session_open events only, so it never fired on resume. The `session_data` branch with `sessions_opened == 0 and not _reconnect_detected` now schedules the same 150 ms device-init timer, so VGS/CSP/KSP all re-emit catalog + 0x09 state push on resume.

### configJson state push includes top-level fields many docs omit

Doc § 857 captures the full 11-key schema (`TitleId, configJsonList, disableManager, displayVersion, enableManager, fontRefMap, imagePath, imageRefMap, resetVersion, rootDirPath, sortTag`). Earlier sim builds only emitted 5 (TitleId, configJsonList, disableManager, displayVersion, enableManager) and PitHouse rejected the state / failed to progress tier def. All 11 fields must be present; factory-fresh values for the missing 6 are `fontRefMap={}, imagePath=[], imageRefMap={}, resetVersion=10, rootDirPath="/home/moza/resource", sortTag=0`.
