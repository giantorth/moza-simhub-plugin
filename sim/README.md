# Wheel Simulator — Development Guide

Reference for working on `sim/wheel_sim.py` and related USB emulation infrastructure.

---

## Purpose

The simulator (`sim/wheel_sim.py`) serves two overlapping goals:

1. **Plugin development / regression testing** — Acts as a MOZA wheel over a virtual COM port so the plugin can run on Linux (via Proton) without real hardware. Decodes and prints received telemetry frames, letting you verify the plugin is sending correct data.

2. **PitHouse traffic capture** — Acts as a real device convincingly enough that PitHouse (the official MOZA config app) sends its complete startup sequence, so you can capture and study traffic patterns not visible from plugin-only captures. This requires PitHouse to enumerate the device via WMI (VID=0x346E), which means a real USB device is needed — see "USB gadget" below.

---

## Simulator architecture

### Response dispatch priority

`WheelSimulator.handle(frame)` dispatches in this order:

1. **Firmware debug** (group `0x0E`) — silently consumed. PitHouse base debug console output.
2. **Heartbeat** (group `0x00`, empty payload) — ACK only for simulated devices (0x12, 0x13, 0x17). ACKing phantom devices causes PitHouse to endlessly probe their identity.
3. **Keepalive** (group `0x43`, payload `0x00`) — ACK for simulated devices.
4. **`_handle_wheel()`** — hardcoded responses: session open → `fc:00` ack (echoes host's open_seq) and schedules device-side session opens on the second host open; incoming `fc:00` ACKs → silent consume; session data → `UploadTracker` feed + chunk reassembly + tier def parsing + FF-chunk timer that queues the captured-reply frames after idle; type=0x00 end marker on session 0x04 → parse uploaded mzdash, add to `stored_dashboards`, queue configJson state refresh on session 0x09; display probe `0x07:0x01` → identity `0x87`; telemetry `7D:23` → decode. Returns `None` if unrecognized. The per-call return includes drain from `_pending_sends` so timer-queued frames ride out alongside the synchronous response.
5. **Wheel write echo** (`_WHEEL_ECHO_PREFIXES`) — group `0x3F`/`0x3E` writes (LED colors, brightness, channel enables, display config) echoed verbatim with response group.
6. **Base settings echo** (group `0x29` to dev `0x13`) — PitHouse hub config writes, echoed verbatim.
6a. **Pedal settings echo** (group `0x24` to dev `0x19`) — PitHouse pedal calibration writes (~50 Hz stream of 25 sub-cmds during slider drags), echoed verbatim. Active only when the model has a `pedal_identity` block (KS Pro currently). Without this PitHouse retransmits each cmd >1000× per session waiting for an ack.
7. **Replay table** (`ResponseReplay`) — exact `(group, device, payload)` lookup from PCAPNG captures. First-observed response wins.
8. **Wheel config echo** (group `0x40` to dev `0x17`) — fallback for config reads not in replay table. Echoes payload with group `0xC0`. Catches LED config queries with variable payloads (CSP pages 0-3, brightness reads, etc.).
9. **Unhandled counter** — if all above miss, increment `unhandled_counts[(group, device, hex_payload)]`.

### Replay table (`ResponseReplay`)

Loaded from two sources:

**PCAPNG** via tshark (`load_pcapng`). Pairs host→device frames with device→host frames by:
- Timestamp proximity (250ms window)
- Expected response group = `req_group | 0x80`
- Expected response device = `swap_nibbles(req_device)`

**JSON replay tables** via `load_json` — schema v1 format, one file per target device byte with entries keyed by `<group_hex>:<req_payload_hex>`:

```json
{
  "schema": 1,
  "device": 23,
  "label": "wheel (device 0x17)",
  "source": "usb-capture/<origin>.pcapng",
  "entries": {
    "40:2800": "7e04c07128020011",
    ...
  }
}
```

First-observed response per `(group, device, payload)` key wins across all sources. Models specify their JSON tables via `replay_tables: [...]` on the profile; loader is invoked from standalone `main()` (first, so JSON wins on key collisions) and again from `mcp_server._apply_model` per-model.

Extractor tool: `sim/extract_replay.py <capture.pcapng> --prefix <name>_ [--device 0x17] [--group 0x40]`. Walks a capture, pairs host/device frames, writes one consolidated JSON file per device byte observed.

**Current ship set** (`sim/replay/`):

| Model | Hub (0x12) | Base (0x13) | Wheel (0x17) | Pedal (0x19) | Total |
|-------|-----------|-------------|--------------|--------------|-------|
| kspro | 99 | 111 | 354 | 92 | 656 |
| csp_r9 | 94 | 135 | 2003 | — | 2232 |

**Self-test pass criteria**: `--replay-handshake` counts "missed replay" only when a frame whose key IS in the table fails to get a replay hit. Frames with no expected response (writes) don't count as failures — those are "orphans".

### Plugin probe commands (synthetic acks)

> Note: These probes are **plugin-specific behavior**, not PitHouse. Emitted by `ProbeMozaDevice()` in `MozaSerialConnection.cs` before the plugin opens a session. PitHouse uses its own VGS identity probes (groups 0x02/0x04/…/0x11 → device 0x17) instead. Plugin probe shape may change in future revisions — re-verify if probe fallback breaks.

| Probe | Group | Device | Payload |
|-------|-------|--------|---------|
| Base | 0x2B | 0x13 | `01 00 01` |
| Hub | 0x64 | 0x12 | `03 00 00` |

Not present in replay table (capture taken with full device attached — device responded before probe cycle completed). Sim handles them via synthetic framed echoes in `_PROBE_SYNTH` (`wheel_sim.py`), sufficient because `ProbeMozaDevice` only checks first byte == `0x7E`.

---

## MOZA protocol quick reference

For full details: `docs/protocol/`.

### Frame format

```
7E [N] [group] [device] [N payload bytes] [checksum]
```

Checksum: `(0x0D + sum_of_all_preceding_bytes) % 256`

Host → wheel: group=`0x43`, device=`0x17`
Wheel → host: group=`0xC3` (= `0x43 | 0x80`), device=`0x71` (nibble-swap of `0x17`)

### Session protocol

- `7C:00` type=`0x81` = session open request (either direction)
- `7C:00` type=`0x01` = data chunk (either direction)
- `7C:00` type=`0x00` = session end/close marker
- `fc:00 [session] [ack_lo] [ack_hi]` = session cumulative ACK — **both directions emit**. Host periodically sends `43:17:fc:00:[sess][seq]` meaning "I've acked up to seq X on session Y"; wheel emits `c3:71:fc:00:[sess][seq]` in reply. Sim's `_handle_wheel` consumes inbound `fc:00` silently — letting them fall through to the replay table returns a stale capture-time ack value that triggers PitHouse retransmit loops.

**Session roles** (verified across 2025-11 and 2026-04 firmware captures —
KS Pro reshuffled the map; see [docs/protocol/sessions/lifecycle.md](../docs/protocol/sessions/lifecycle.md) for full per-firmware table):

2025-11 firmware (VGS, CSP, older displays):

| Session | Opened by | Role | Notes |
|---------|-----------|------|-------|
| `0x01` | host | Management (wheel identity / log stream / channel catalog push from PitHouse) | |
| `0x02` | host | Telemetry (tier def + `fc:00` acks) | |
| `0x03` | host | Aux config (tile-server state push, 12-byte envelope) | |
| `0x04` | device | Directory listing + dynamic file-transfer | |
| `0x05` / `0x06` / `0x07` (dynamic) | device, triggered by host `7c:23` | File transfer | |
| `0x08`, `0x0A` | device | Keepalive / control / RPC / Schema B deltas / tile-server mirror | |
| `0x09` | device | **configJson Schema A snapshot** (9-byte envelope) | |

2026-04+ firmware (KS Pro):

| Session | Opened by | Role | Notes |
|---------|-----------|------|-------|
| `0x01` | host | Management — channel catalog push host→dev (zlib binary); wheel debug log dev→host (zlib UTF-16) | |
| `0x02` | host | Telemetry | |
| `0x03` | host | **UNUSED** — open + 4-byte zero keepalives only | (older firmware's tile-server channel) |
| `0x04` | host (uploads + dir-query) / device (dir-reply) | **Tile-server push host→dev** (no-op — KS Pro display has no map UI) + dir-listing reply dev→host | Multi-purpose by direction |
| `0x05` (dynamic) | device, triggered by host `7c:23` | **File transfer** | Replaces 0x04 in upload role on KS Pro |
| `0x06`, `0x07`, `0x08` | both | Reserved keepalive | |
| `0x09` | device | **UNUSED** — open + 4-byte zero keepalives only | (older firmware's state-push channel) |
| `0x0A` | device | **configJson Schema A snapshot + Schema B deltas + RPC** — all wheel-state traffic consolidated. Same 9-byte envelope. | `WHEEL_MODELS['kspro'].configjson_session = 0x0a` |
| `0x0B` | device | **Tile-server mirror dev→host** (12-byte envelope) | New on KS Pro; mirror only — wheel doesn't actually use the data |

Sim opens its device-side sessions via `resp_device_session_open(session, port)` ~150 ms after the host finishes opening 0x01/0x02. For session `0x05`/`0x06`, the sim waits for the host's `7c:23 46 80 XX 00 YY 00 fe 01` trigger and opens session `YY` on demand — real firmware does the same. Port byte duplicated in the open payload; the constant `FD 02` trailer is required. See `docs/protocol/findings/2026-04-24-firmware-upload-path.md`.

### Dashboard upload + configJson

Upload flow (2026-04+ firmware; session number varies):

1. Host sends dir-listing probe on session `0x04` — sub-msg type=`0x08` asking for `/home/root`.
2. Sim replies type=`0x0a` via `build_dir_listing_reply(echo_id)` (221B captured payload with fresh echo_id).
3. Host sends `7c 23 46 80 XX 00 YY 00 fe 01` (port=`YY` session-open request).
4. Sim emits device-initiated session open for session `YY` (0x05 on current PitHouse, 0x06 on 2025-11 captures).
5. Host sends sub-msg type=`0x02` metadata on session `YY` — LOCAL path (`0x8C` marker on 2026-04+, `0x8A` on 2025-11) + md5 + bytes_written=0 + total_size BE + sentinel.
6. Sim replies type=`0x01` ready-ack via `_queue_file_transfer_echo` + `build_file_transfer_response`.
7. Host sends type=`0x03` content (zlib-compressed mzdash body).
8. Sim replies type=`0x11` content-complete ack (bytes_written == total_size).
9. Both sides exchange `7c:00 [session] 00` end markers.

Sim decodes uploaded zlib blobs via `UploadTracker`; dashboard name extracted from upload metadata path. Three shapes supported by `extract_mzdash_path`:
- old firmware remote: `/home/(moza|root)/resource/dashes/<name>/<name>.mzdash`
- 2025-11 remote: `/home/(moza|root)/resource/dashes/<name>.mzdash`
- 2026-04 PitHouse: no remote path emitted at all — only a Windows-side stage path `<...>/MOZA Pit House/_dashes/<hash>/dashes/<name>/<name>.mzdash` from which `<name>` is parsed.

Decoded blobs visible through `sim_uploads`. See `docs/protocol/findings/2026-04-24-firmware-upload-path.md` for byte-level wire format.

**Status 2026-04-27 — partial regression on KS Pro:** small uploads work but large multi-chunk uploads stall mid-stream against KS Pro PitHouse (chunk count ranges from 1 to 16 before PitHouse stops sending). Root cause un-identified — sim emits per-round progress acks correctly, content accumulates in the buffer, but PitHouse stops streaming after some host-internal flow-control trigger. See [usb-capture/upload-protocol-re.md](../usb-capture/upload-protocol-re.md) for the open-question summary. Mitigation in place:

- **Trial-decompress in `_queue_file_transfer_echo`** — once the accumulated content decompresses cleanly with `decoded_size + 64 >= total_size`, sim fires `type=0x11` done-ack. Exits the upload cleanly when content really did arrive in full.
- **`SESSION_TYPE_END` gated on `_ft_rounds_acked == 0xFFFF`** — sim no longer writes partial/corrupt files to the virtual FS when PitHouse aborts mid-stream. Aborted uploads now emit an `upload_aborted` event via the emitter (visible through `sim_recent`).
- Per-session state cleanup on session end (`_ft_rounds_acked.pop`, `_ft_reply_next_seq.pop`, `_mid_upload_dirname.pop`) so the next OPEN starts clean.

**Resolved 2026-04-24 — small upload pipeline works end-to-end.** Dashboard upload completes against PitHouse for short single-chunk transfers; sim writes the decoded mzdash body into its virtual FS. Fixes landed:

1. **1-byte XOR status, not 4-byte trailer.** The mystery `93 71 e8 bd` / `fa cd 10 c6` bytes earlier docs chased were 1B of XOR checksum + 3B of the next chunk's 4-byte CRC32 misread by a buggy `extract_frames` helper that dropped the last 2 bytes of every frame. Real layout: message ends with `ff ff ff ff` sentinel + 1B `status = xor(body_bytes)`.
2. **`size_LE = msg_len - 6`.** The "header" is effectively 6 bytes (type + size + 1B pad); the two `00 00` bytes that look like more pad are actually body start. Sim keeps an 8B header shape internally and sets `size = body_len + 2`.
3. **4-byte CRC32-LE per chunk, not 3 bytes.** The 3-byte-truncated interpretation was the same capture-extract artifact as (1). All paths (`chunk_session_payload`, `_chunk_catalog_message`, `UploadTracker.feed`, `PitHouseUploadReassembler.add`) use 4-byte CRC.
4. **Dynamic file-transfer session port.** Sim no longer hardcodes `{0x05, 0x06}`; any session in `0x04..0x0a` is treated as a file-transfer candidate and gated by whether the buffer contains a type=0x02 sub-msg (2026-04 PitHouse has landed uploads on session 0x07 in live runs).
5. **Device-side reply seq starts at `port + 1`.** Separate counter from the host's session seq; earlier reuse of `_upload_next_seq` (host counter) put replies at `host_last + 1` which PitHouse silently dropped.
6. **Per-round type=0x01 progress acks for large uploads.** PitHouse splits a 500KB mzdash across dozens of type=0x03 sub-msgs and won't continue past the first round without a wheel ack containing advancing `bytes_written`. Sim extracts decompressed byte count from the accumulated zlib stream each time `_queue_file_transfer_echo` fires and emits type=0x01 with that bw; final type=0x11 done-ack only fires once the deflate stream reaches EOF.
7. **Anchored type=0x02 path scan.** `_scan_file_transfer_paths` locates the LAST type=0x02 metadata boundary in the session buffer before walking path TLVs, so retry attempts don't conflate fresh paths with stale ones from prior aborted attempts.
8. **Zlib stream reassembly across split sub-msgs.** Only the first type=0x03 sub-msg carries `78 9c` zlib magic; continuations are raw deflate bytes at the same byte offset within their sub-msg (path TLVs + md5 + tokens + compressed_header in front, then deflate bytes). `_parse_upload` anchors on the first sub-msg, derives the intra-msg zlib offset, concatenates continuations using each sub-msg's own `size_LE`, and feeds into `zlib.decompressobj()` with partial-output tolerance (PitHouse-aborted transfers still yield whatever bytes arrived).
9. **Session-open via `7c:23` now registers the port** in `device_opened_sessions`, so the session_end handler runs `_parse_upload` when the upload completes on a dynamically-opened session.

See `docs/protocol/dashboard-upload/upload-handshake-2026-04.md` and `docs/protocol/sessions/chunk-format.md` for byte-level layout and the verification corpus.

**Known workflow quirk.** Sim reload (`sim_reload` → `sim_start`) appears to Windows as a USB disconnect/reconnect; PitHouse re-enumerates cleanly. This is required after any upload abort because PitHouse caches mid-flight "upload in progress" state and won't re-handshake until it sees the wheel drop. Also **after restarting PitHouse**, sim must be reloaded once — otherwise PitHouse partially detects the wheel (display present but tier_def never arrives). Investigation pending.

Session 0x09 / 0x0a configJson is a separate RPC (session number is firmware-version-dependent — see `configjson_session` field below):
- Device pushes state JSON — 9-byte envelope (`flag + comp_size + uncomp_size`) + zlib stream. **Two schemas emitted by the same wheel**:
  - **Schema A snapshot** at connect: `{TitleId, configJsonList, disableManager, displayVersion, enableManager, fontRefMap, imagePath, imageRefMap, resetVersion, rootDirPath, sortTag}` — built by `build_configjson_state(...)`.
  - **Schema B deltas** on FS mutations (KS Pro only — older firmware re-emits Schema A): `{TitleId, disabledManager, enabledManager, imagePath}` with `enabledManager.updateDashboards` carrying the full current list and `enabledManager.deletedDashboards` carrying any removed id. Built by `build_configjson_state_schema_b(...)`. `_fire_state_refresh` branches on `_configjson_session` to pick the right shape.
  - **Schema B mid-upload phase 1**: `_maybe_fire_schema_b_phase1(session)` fires while chunk-0's bundle file table is parseable but content is still streaming — `disabledManager.updateDashboards` carries the in-flight dashboard.
  - **Schema B transitional phase 2**: empty `disabledManager` + empty `enabledManager` fired post-commit before the re-enumerate.
  - Full field semantics in [usb-capture/payload-09-state-re.md](../usb-capture/payload-09-state-re.md).
- Host replies with compressed `{"configJson()": {"dashboards":[...], ...}, "id": 11}` list. Sim stashes id→dirName mappings into `_pithouse_dashboard_ids` so subsequent `completelyRemove(id)` RPCs can resolve PitHouse-assigned ids back to dirNames; sim does NOT mirror the host's list as authoritative (earlier versions wiped real user uploads since the host's "library" is its own profile, not the wheel's FS). If the reply carries an `activeDashboardId` / `activeDashboard` / `active` signal — int slot or string id — sim updates `active_dash_index` from it (best-effort; exact field name still under RE since real captures rarely show a switch event).

#### Factory ROM vs FS — separation of concerns

Real wheel reports a hybrid dashboard list in the 0x09 state push: the firmware-baked **factory** entries (RGB-DU-V11 ships 11 dashboards: Rally V1–V6 + Core/Mono/Pulse/Nebula/Grids; older SM-DU-V14 ships 12 different ones) come from the display module's ROM and are NOT queryable as filesystem files. **User uploads** are written to `/home/moza/resource/dashes/<dirName>/<dirName>.mzdash`.

The sim mirrors this split:

- **Factory state JSON** ([sim/factory_state_w17_rgb.json](factory_state_w17_rgb.json) for VGS/CSP/KSPro, [sim/factory_state_w08_sm.json](factory_state_w08_sm.json) for the older display module) — full configJson state captured byte-exact from real hardware. Plays the ROM role. `WHEEL_MODELS[<wheel>].factory_state_file` selects which one to load.
- **Virtual FS** (`WheelFileSystem`, persisted to `sim/logs/wheel_fs.json`) — starts EMPTY on first run; tracks user uploads only. Path layout matches real wheel: `/home/moza/resource/dashes/<dirName>/...`.
- **`build_configjson_state(fs.dashboards(), factory_file=...)`** — emits factory `enableManager.dashboards[]` first (firmware-baked), then appends FS-tracked user uploads with synthesised metadata. Top-level `imageRefMap`/`imagePath`/`fontRefMap` and per-manager `imageRefMap`s come from factory verbatim. Output byte-exact to real-wheel capture (1709B compressed / 7231B uncompressed for Set A; 3039B / 15462B for Set B).
- `WheelFileSystem.purge_legacy_dash_paths()` runs at sim init to clean up `/home/root/resource/dashes/...` entries left by sim builds before 2026-04-25 (path migration).
- Opt-in `WheelFileSystem.seed_factory_dashboards()` test helper writes stub mzdash files into the FS (with canonical hashes from factory state). Not auto-called — sim defaults to empty FS, factory entries stay in JSON ROM analog.

The session 0x04 root-dir query (`/home/root`) is unrelated to dashboard storage. Real wheel returns just `temp/` for that path; the captured 221B reply is replayed by `build_dir_listing_reply`.

**Storage display in PitHouse — likely client-side compute.** PitHouse's Dashboard Manager UI shows occupied/free storage for the wheel, but **searches across both KS Pro pcaps + latestcaps found no wire signal carrying a plausible MB byte count**. The 14-byte pre-zlib metadata block in the session 0x04 dir-listing reply (4-byte LE u32 at offset 10: 100,528 on KS Pro, 100,521 on 2025-11, 273,966 on older) is in the ~100 KB range — way smaller than the 50+ MB displayed. PitHouse most likely computes the value locally from its own per-wheel upload cache (sum of `.mzdash` + asset sizes for each upload it has performed). Sim has no obligation to send it. See [usb-capture/ksp-deep-investigation-plan.md § Storage display value](../usb-capture/ksp-deep-investigation-plan.md).

### Tier definition (v2)

TLV format inside session data chunks:

| Tag | Meaning | Layout |
|-----|---------|--------|
| `0x01` | Tier | `tag(1) + size(4) + flag(1) + channels((size-1)/16 * 16)` |
| `0x00` | Enable | `tag(1) + value(4) + flag(1)` |
| `0x06` | End marker | `tag(1) + param(4) + total(4)` — **NOT a hard stop** |
| Other | Preamble / skip | Generic TLV: `tag(1) + param(4) + data(param bytes)` |

**Critical**: Do NOT break on tag `0x06`. The session data can contain a probe batch followed by the real tier def, both ending with `0x06`. Treat `0x06` as a generic skip.

Preamble tags `0x07` and `0x03` are sent as a separate message before the tier def. Parsing them byte-by-byte causes false detection of `0x00`/`0x01` tags inside preamble data.

### Telemetry frames

Format: `7D 23 32 00 23 32 [flag] [0x20] [bit-packed data]`

Flags observed: `0x00`, `0x02`, `0x03`, `0x04` — all can be active simultaneously (each corresponds to a tier with different update frequency).

---

## Live mode: Linux + Proton

**tty0tty** creates real `/dev/tntN` character devices that Wine/Proton accept (unlike socat ptys, which are rejected at `CreateFile` time because `/dev/pts/*` lacks serial ioctls).

### One-time install (Arch/DKMS example)

```bash
yay -S tty0tty-dkms-git                            # AUR; DKMS rebuilds on kernel updates
sudo modprobe tty0tty                              # creates /dev/tnt0..7 (pairs 0↔1, 2↔3, 4↔5, 6↔7)
echo tty0tty | sudo tee /etc/modules-load.d/tty0tty.conf   # auto-load at boot

# Grant your user access — group varies by distro
ls -l /dev/tnt0                                    # note the group (often tty or uucp)
sudo usermod -aG <group> $USER                     # log out/in for the change to take effect
```

Other distros: the module is standard DKMS so `dkms install` works — see <https://github.com/lcgamboa/tty0tty>.

### Per-run

SimHub runs as a non-Steam game via Proton. The prefix is typically:

```
~/.steam/steam/steamapps/compatdata/<appid>/pfx/
```

Find it (non-Steam games use large synthetic appids):

```bash
find ~/.steam/steam/steamapps/compatdata/*/pfx/drive_c \
     -maxdepth 5 -iname 'SimHub*.exe' 2>/dev/null
```

Point COM3 in that prefix at `/dev/tnt1` (overwrites the default `/dev/ttyS2` link) and run the sim on the other end of the pair:

```bash
PREFIX=~/.steam/steam/steamapps/compatdata/<appid>/pfx
ln -sf /dev/tnt1 "$PREFIX/dosdevices/com3"
python3 sim/wheel_sim.py /dev/tnt0
# Launch SimHub — it enumerates COM3 and connects.
```

Restore default COM3 with `ln -sf /dev/ttyS2 "$PREFIX/dosdevices/com3"`.

### Troubleshooting

- `Permission denied` opening `/dev/tnt0` — not in the group that owns it; redo `ls -l` + `usermod -aG` and log out/in.
- SimHub doesn't see the COM port — wrong prefix; verify `find` result and re-symlink inside the correct `compatdata/<appid>/pfx/`.
- `modprobe tty0tty` fails on a new kernel — prefer `tty0tty-dkms-git` (AUR git package) which tracks kernel compat fixes.

---

## Live mode: Windows

Create a virtual COM pair with [com0com](https://sourceforge.net/projects/com0com/) (e.g. COM10 ↔ COM11). Point SimHub/PitHouse at COM10, run the sim on COM11:

```
python sim\wheel_sim.py COM11
```

Note: com0com works for **SimHub** only. PitHouse filters devices by WMI on `VID_346E%` and ignores virtual COM ports — for PitHouse capture on Windows, use USBIP (below).

---

## USB gadget: PitHouse traffic capture

PitHouse detects MOZA devices via WMI on Windows (`SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE '%VID_346E%'`). Virtual COM ports and tty0tty do not satisfy this — only a real USB device with the correct VID will make PitHouse enumerate the device fully and send its complete startup sequence.

### Pipeline

```
wheel_sim.py ↔ /dev/ttyGS0 ↔ libcomposite (CDC ACM) ↔ dummy_hcd ↔ usbipd
                                                                    ↕  (TCP/3240)
                                                                 usbip-win2 → PitHouse
```

**Linux side**: `sim/setup_usbip_gadget.sh` creates a CDC ACM gadget via configfs with VID `0x346E` PID `0x0006`, binds it to `dummy_hcd`, starts `usbipd`. The ACM interface appears as `/dev/ttyGS0`. Run `python3 sim/wheel_sim.py /dev/ttyGS0`. Tear down with `sim/teardown_usbip_gadget.sh`.

**Windows side**: install the signed `usbip-win2` kernel driver, then `usbip attach -r <linux-ip> -b 1-1`. Windows sees a USB CDC device with `VID_346E&PID_0006`, a COM port appears, PitHouse's WMI scan picks it up.

Full step-by-step runbook (prerequisites, troubleshooting, capture workflow): [`sim/USBIP_SETUP.md`](../sim/USBIP_SETUP.md).

---

## Capture files reference

See `usb-capture/CAPTURES.md` for the full list. Key files:

| File | Contents | Replay entries |
|------|----------|----------------|
| `usb-capture/12-04-26/moza-startup.pcapng` | Full MOZA base+wheel startup | 775 (primary replay table) |
| `usb-capture/connect-wheel-start-game.pcapng` | Connect wheel + game start | Used in `--replay-handshake` self-test |
| `usb-capture/vgs-to-cs.pcapng` | VGS→CS device exchange | Reference only |
| `usb-capture/09-04-26/dash-upload.pcapng` | Legacy (2026-04) dashboard upload flow | Reference for old session 0x01 upload + old configJson schema |
| `usb-capture/latestcaps/automobilista2-wheel-connect-dash-change.pcapng` | 2025-11 firmware wheel connect + dashboard change | Reference for dir-listing type=0x08/0x0a exchange + 2025-11 configJson schema. Upload bytes here rode on session 0x06; 2026-04 firmware moved them to session 0x05. |
| `usb-capture/latestcaps/automobilista2-dash-change.pcapng` | Warm dashboard switch (no open/close) | Reference for configJson state mutation on active dashboard change |
| `usb-capture/latestcaps/pithouse-switch-list-delete-upload-reupload.pcapng` | 2026-04 CSP + R9 base full PitHouse UI session (switch / delete / upload / re-upload dashboards) | Source for `sim/replay/csp_r9_*.json` (2232 entries). Display cascade, session 0x06 file paths, hub + base identity bytes. |
| `usb-capture/ksp/putOnWheelAndOpenPitHouse.pcapng` | KS Pro (W18) boot + PitHouse connect | Source for `kspro` profile + `sim/replay/kspro_*.json` (656 entries). Has pedal (dev 0x19) traffic. |

The captures use `usbcom.data.out_payload` (host→device) and `usbcom.data.in_payload` (device→host). tshark must extract both fields — see `extract_from_pcapng()` in `wheel_sim.py` for the exact tshark invocation.

---

## Known working state

- `--validate`: parses frames from PCAPNG, prints telemetry decode.
- `--replay-handshake` / `--replay-self-test`: 0 missed replay hits on `moza-startup.pcapng`.
- Live mode via tty0tty + Proton: known-working for SimHub plugin testing.
- USBIP gadget scripts (`setup_usbip_gadget.sh`, `teardown_usbip_gadget.sh`) written and syntax-clean; **not yet validated end-to-end against real PitHouse on Windows**.
- Hardcoded probe responses: plugin base/hub probes (`_PROBE_SYNTH`) and PitHouse identity probes (`_PITHOUSE_ID_RSP`, built from `--model` selection) live in `wheel_sim.py`.
- Multi-model support: `--model vgs` (default), `--model csp`, `--model ks`, `--model kspro`, or `--model es`. Identity strings, capability flags, and hardware IDs are derived from the selected model profile (`WHEEL_MODELS` dict).
  - **VGS** — identity + sub-device values + session-1/2 replay extracted from `usb-capture/connect-wheel-start-game.pcapng`.
  - **CSP** — identity rebuilt 2026-04-24 from `usb-capture/latestcaps/pithouse-switch-list-delete-upload-reupload.pcapng` (hw_id, serials, dev_type `01:02:06:06`, display dev_type `01:02:11:06` — verified byte-exact). Ships with 3 JSON replay tables in `sim/replay/csp_r9_*.json` covering hub + base + wheel identity cascade (2232 entries).
  - **KS** — identity captured live from real R5 base + KS wheel via `sim/probe_wheel.py` (2026-04-20) — no dashboard, no display sub-device.
  - **KS Pro (W18)** — added 2026-04-23 from `usb-capture/ksp/putOnWheelAndOpenPitHouse.pcapng`. Shares W17-HW RGB display hardware with CSP (same 12B display hw_id; redacted in source). Wheel-head identity bytes re-extracted byte-exact 2026-04-26: `dev_type 01:02:07:07`, `caps 01:02:41:01`, hw_id redacted, `hw_sub U-V10`, `display.dev_type 01:02:11:06`. KS Pro emits its own 3-variant `7c:23` page-activate set with `fc 03` trailer (vs CSP's 2-variant `fe 01`) — uses `_7c23_frames_name: 'KSPRO'`. PitHouse appears to gate dashboard detection on the wheel-specific trailer; using CSP frames was enough to leave the dashboard manager partially blank. 4 JSON replay tables in `sim/replay/kspro_*.json` (656 entries) including pedal (dev 0x19, 92 entries — KS Pro captures show real pedal traffic).
  - **ES** — identity captured live from real R5 base + ES wheel (2026-04-23) — see ES caveat below.
- Plugin ack race condition: fixed in `Telemetry/TelemetrySender.cs`.
- Device-initiated session opens: sim proactively opens sessions 0x04/0x06/0x08/0x09/0x0A 150 ms after host brings up 0x01/0x02 — required for PitHouse's dashboard UI (session 0x09 populates `configJsonList`) and for the plugin's session 0x04 upload path. Trigger fires from EITHER `sessions_opened >= 2` in the session_open path OR `sessions_opened == 0 && !_reconnect_detected` in the session_data path; the latter handles plugin's resume-existing-sessions flow on sim restart (see protocol doc § Cold-start: PitHouse skips tier_def push on reconnect).
- Dashboard upload decode: `UploadTracker` reassembles FF-prefixed chunks, decompresses zlib, parses mzdash JSON + path. Uploads are persisted to `sim/logs/stored_dashboards.json` and surface through `sim_uploads` / `sim_stored_dashboards`.
- configJson schemas: sim emits and parses both the 2026-04 (`disabledManager`/`updateDashboards`) and 2025-11 (`disableManager`/`dashboards`/`configJsonList`/`displayVersion`) variants. Active schema is chosen from the most recent observed state.

---

## Wheel model profiles (`WHEEL_MODELS`)

Each profile in `sim/wheel_sim.py` declares the bytes the sim emits for one wheel. PitHouse is strict: mismatches on any of the fields below cause either total mis-identification or "partially detected" states (e.g. wheel correct but display empty in the dashboard management tab).

### Identity fields — match real hardware exactly

| Field | Probe | Response group | Extract from pcapng |
|-------|-------|----------------|---------------------|
| `name` | `0x07 0x17 [01]` | `0x87` | 16-byte ASCII, null-padded. PitHouse also sends short form `0x07 0x17` (no sub-byte) — sim entry `(0x43, 0x17, b'\x07')` returns same content |
| `sw_version` | `0x0f 0x17 [01]` | `0x8f` | 16-byte ASCII. Short form `0x0f 0x17` (no sub) also handled |
| `hw_version` | `0x08 0x17 [01]` | `0x88` | 16-byte ASCII. Short form `0x08 0x17` also handled |
| `hw_sub` | `0x08 0x17 [02]` | `0x88` | 16-byte ASCII |
| `serial0` | `0x10 0x17 [00]` | `0x90` | 16-byte ASCII — **must be real serial, placeholders break detection**. Short form `0x10 0x17` also handled, defaults to sub=0x00 |
| `serial1` | `0x10 0x17 [01]` | `0x90` | 16-byte ASCII |
| `caps` | `0x05 0x17 [00 00 00 00]` | `0x85` | 4 bytes — bit `0x20` of byte 2 advertises a detachable RGB display (CSP); without it (VGS/KS) PitHouse skips the sub-device probe cascade |
| `hw_id` | `0x06 0x17` | `0x86` | Variable-length bytes — CSP real HW returns 12 bytes (`80 31 3b c0 00 20 30 04 4a 36 30 34`). Earlier docs had this as 8 bytes — wrong, correctly re-extracted 2026-04-24 |
| `dev_type` (optional) | `0x04 0x17 [00 00 00 00]` | `0x84` | 4 bytes. VGS = `01:02:04:06`, **CSP = `01:02:06:06`** (corrected 2026-04-24 — earlier sim default `01:02:04:06` was wrong for CSP), KS = `01:02:05:06`, **KSP = `01:02:07:07`** (corrected 2026-04-26 against `usb-capture/ksp/putOnWheelAndOpenPitHouse.pcapng` — earlier sim default `01:02:05:06` made PitHouse demote KSP to a generic KS model after the initial name-based match), ES = `01:02:12:08`. Set explicitly on new profiles if real HW differs |
| `identity_11` (optional) | `0x11 0x17 [04]` | `0x91` | Defaults to `04:01` (VGS/CSP). Real VGS/CSP both return this; `00:00` makes PitHouse mis-identify VGS. KS real HW returns `04:00`. PitHouse also sends short form `0x11 0x17` (no sub) — sim returns `0x91 0x04` (2 bytes, no trailing `0x01`) |

**2026-04 firmware sends short-form probes** (no sub-byte) for cmd `02/07/08/0f/10/11` in place of the older sub-byte variants. See `docs/protocol/findings/2026-04-24-csp-deep-dive.md` (§ Short-form identity probes) for aggregate counts across captures — the sub-byte variants `08:01`, `10:00`, etc. appear **zero** times in any capture; only short forms are observed. Sim now handles both (short form is required; sub-byte is dead code kept for historical firmware compat).

### Display sub-device (nested `display` dict)

Sent under `(group=0x43, device=0x17)` when SimHub plugin's `SendDisplayProbe` fires. PitHouse also issues the cascade when caps bit `0x20` is set.

- `name`, `sw_version`, `hw_version`, `hw_sub`, `serial0`, `serial1`, `dev_type`, `caps`, `hw_id` — must be per-model real-hardware values, **not** a copy of the wheel's own `hw_id` or placeholder strings. A placeholder display `hw_id` was enough to make PitHouse report VGS as a wrong-model wheel.

### Proactive wheel-initiated frames

- `emits_7c23` (bool) + `_7c23_frames_name` (`"VGS"` | `"CSP"` | `"KSPRO"`): when True, the sim emits the model's dashboard-activate page frames at startup and a ~1 Hz periodic cycle after catalog upload. Byte 2 after `7c 23` differs per wheel (VGS page 1 = `0x32`, CSP page 1 = `0x3c`, KSP page 1 = `0x32` followed by 0x80 0x04). The trailer also varies: VGS/CSP emit `fe 01`, KSP emits `fc 03`. Page count: VGS 3, CSP 2, KSP 3. Using the wrong set makes PitHouse mis-enumerate dashboard pages and fail to "fully detect" the display (KSP dashboard manager left empty when sim sent CSP frames — verified 2026-04-26). Set `False` for wheels without a dashboard screen (KS); passive capture against real KS showed zero `7c:23` frames.
- `session_layout` (`"legacy"` | `"vgs_combined"`): controls `build_device_catalog`:
  - `"legacy"` (default, CSP): session 1 carries the device description, session 2 carries the channel URL catalog.
  - `"vgs_combined"` (VGS): session 1 carries the tiny seed (`ff` + a 9-byte control TLV); session 2 carries the device description TLVs (split at real-HW boundaries 26/5/2/9/2 bytes, each chunk with its own CRC-32) followed by the channel catalog.
- `session2_desc_chunks` (tuple, optional): override the default VGS 26/5/2/9/2 split for a different desc length. CSP desc is 42 bytes — correct split is `(24, 5, 2, 9, 2)` with chunk 1 ending on `0x0a` (not `0x05` — that was a mistranscription in older profiles). KSP desc is 44B with VGS-style split.
- `replay_tables` (list[str], optional): paths (relative to repo root) to JSON replay tables (schema v1). Loaded by `ResponseReplay.load_json` at sim_start, before pcapng fallback. Used for hub/base/wheel/pedal identity cascades extracted from real captures — enables answering dev 0x12/0x13/0x19 probes in addition to dev 0x17. See `sim/replay/` for shipped tables. Extract your own with `sim/extract_replay.py <capture> --prefix <model>_`.
- `catalog_pcapng` (string, optional): path (relative to repo root) to a real-hardware capture. When set, the sim replays session 1 and 2 wheel→host frames byte-for-byte from the capture instead of synthesizing via `build_device_catalog`. Synth only covers the opening description TLVs; real VGS sends ~150 follow-up TLVs on session 2 that PitHouse waits for before sending the full tier definition. VGS uses `usb-capture/connect-wheel-start-game.pcapng`; CSP falls back to synth (works because PitHouse's CSP path doesn't need the extended session 2 TLVs). Replayed chunk seqs are shifted at send time to match PitHouse's current port counter (see next item).
- `factory_state_file` (string, optional): filename (relative to `sim/`) of the captured configJson state JSON for this wheel's display module. Drives `build_configjson_state` output. Three shipped files:
  - `factory_state_w17_rgb.json` — VGS/CSP (W17/RGB-DU-V11 display, **11** factory dashboards: Rally V1..V6 + Core/Mono/Pulse/Nebula/Grids).
  - `factory_state_kspro.json` — KS Pro (same physical W17 RGB module, but firmware ships **10** dashboards — NO Nebula). Captured byte-exact from `usb-capture/ksp/mozahubstartup.pcapng` 0x0a state push.
  - `factory_state_w08_sm.json` — older firmware (W08/SM-DU-V14 display, 12 dashboards).
  KS Pro requires its own file even though the display PCB matches CSP — substituting the 11-dashboard W17 RGB file leaves PitHouse refusing the state. Wheels without a display (KS, ES) can leave this unset.
- `proactive_session09` (bool, default `True`): when True, sim pushes the configJson state JSON inside the device-init burst (~150 ms after host opens 0x01 + 0x02 OR when proactive_sender unblocks via `_reconnect_detected`). VGS/CSP/KSPro all need this — PitHouse Dashboard Manager UI keys dashboard detection on the state push.
- `configjson_session` (int, default `0x09`): which session the state push lands on. **Firmware-version-dependent** — older 2025-11 firmware (VGS/CSP) uses `0x09`; KS Pro / 2026-04+ firmware moved it to `0x0a` (session 0x09 demoted to empty heartbeats only; tile-server moved from 0x03 to 0x0b at the same time). PitHouse parses state push + dashboard-list reply on whichever session the wheel pushes on; pushing on the wrong session leaves PitHouse with no parsed state and a blank dashboard manager.
- **Per-session wire format also differs:**
  - `0x09` (older): 54B net per chunk + 4B CRC32-LE trailer; first device-side data chunk seq = `0x000a`.
  - `0x0a` (KS Pro): 54B net per chunk, **no CRC trailer**; first device-side data chunk seq = `0x000b`.
  Captured byte-exact from `usb-capture/ksp/mozahubstartup.pcapng` seq 11..69, comp=3171/uncomp=14671. `chunk_session_payload(..., crc_bytes=0)` selects the no-CRC variant.
- **Session 0x0a `fc:00` cumulative-ack heartbeat is mandatory** when `configjson_session=0x0a`. `_emit_session09_keepalive` appends a 3-byte `fc 00 0a` frame alongside the session 0x09 keepalive. PitHouse waits for this heartbeat before sending its own `fc 00 0a [ack:u16]` reply — without it the wheel's state push lands but PitHouse never marks the session as live, so it discards the parsed JSON. Adding the heartbeat alone unlocked PitHouse's dashboard switch + active-dash recognition (verified 2026-04-26).
- `_fire_device_init` triggers from two paths: (1) host session_open with `sessions_opened >= 2`, (2) host session_data with `sessions_opened == 0 and not _reconnect_detected`. Path 2 is required because plugin's recent "Don't preemptively close wheel sessions" change (commit `567ed25`) means PitHouse can resume existing 0x01/0x02/0x03 across sim-restart without re-OPEN. Without path 2, sim never pushes state on resume and dashboard manager goes blank.
- `_fire_device_init` triggers from two paths: (1) host session_open with `sessions_opened >= 2`, (2) host session_data with `sessions_opened == 0 and not _reconnect_detected`. Path 2 is required because plugin's recent "Don't preemptively close wheel sessions" change (commit `567ed25`) means PitHouse can resume existing 0x01/0x02/0x03 across sim-restart without re-OPEN. Without path 2, sim never pushes state on resume and dashboard manager goes blank.
- **Session-open ACK**: the wheel's `fc:00` reply to a session-open must echo the host's open seq (the bytes at payload offset 6–7), **not** constant zero. Real VGS: host opens with seq=N, wheel replies `fc 00 [sess] [N_lo] [N_hi]`. The sim previously hard-coded `ack_seq=0`, which only worked on PitHouse's very first connect (when its port counter happened to be 1); on reconnect the counter incremented to 2+ and PitHouse treated the `ack_seq=0` response as stale, stalling on its stored `ack_seq=3` state and never emitting tier definitions. `_handle_wheel` now passes `open_seq` into `resp_session_ack(...)` for every session open.
- **Session-seq alignment**: PitHouse's session-open payload carries a monotonic port counter (the `[seq_lo] [seq_hi]` bytes right after `[flag_lo] [flag_hi]`). That counter increments on every disconnect/reconnect; the wheel must emit its first chunk at `host_open_seq + 3` on each session. The sim extracts the capture's baseline via `extract_catalog_open_seqs()` and records the runtime value in `WheelSimulator.session_open_seqs` when session opens arrive. `proactive_sender` then calls `rewrite_session_frame_seq()` to shift each replayed chunk's seq by `(host_open_seq - capture_open_seq)` and recompute the checksum. Without this shift PitHouse drops replayed chunks as out-of-order and never sends the full tier definition.

### Adding a new model

Two paths depending on what hardware you have:

**Live hardware attached to Linux** — use `sim/probe_wheel.py` to query the
base/wheel directly over its CDC ACM port. It sends all PitHouse-style
identity probes (name, sw/hw version, caps, hw_id, serials, identity-11) plus
the display sub-device cascade and prints ready-to-paste hex for a new
`WHEEL_MODELS` entry. Example — how the `ks` profile was captured:

```bash
python3 sim/probe_wheel.py /dev/ttyACM0
# Then paste the per-field hex into a new WHEEL_MODELS['<key>'] block.

# Listen for any spontaneous frames (7c:23 dashboard-activate, base debug, …)
python3 sim/probe_passive.py /dev/ttyACM0 --seconds 5
```

`dev_type` (0x04 response) and `identity_11` (0x11:0x04 response) vary per
wheel — KS uses `01:02:05:06` / `04:00` where VGS/CSP use `01:02:04:06` /
`04:01`. Both are optional fields on a model profile; omitting them gets the
VGS/CSP defaults.

#### ES wheel caveat

ES (old-protocol) wheels share device ID `0x13` with the wheelbase — identity
probes to wheel device `0x17` return nothing. Probe `0x13` instead and you get
the **base** identity back (e.g. `R5 Black # MOT-1`, hw `RS21-D05-HW BM-C`).
See `docs/protocol/identity/known-wheel-models.md` (§ ES wheel identity caveat). The captured ES
profile in `WHEEL_MODELS['es']` (R5 base + ES wheel, 2026-04-23) records:

| Field | Value |
|-------|-------|
| name | `R5 Black # MOT-1` |
| sw_version | `RS21-D05-MC WB` |
| hw_version | `RS21-D05-HW BM-C` |
| hw_sub | `U-V10` |
| caps | `01 02 54 00` (no `0x20` RGB-display bit) |
| dev_type | `01 02 12 08` (sub-byte 0x12; VGS/CSP=0x04, KS=0x05) |
| identity_11 | `04 01` (default) |
| hw_id | base hw_id (12 bytes, matches `/dev/serial/by-id/usb-Gudsen_MOZA_*`) |

To re-capture from a different R5/ES combo:

```bash
python3 -c "import sys, time; sys.path.insert(0,'sim'); \
from wheel_sim import build_frame, MSG_START, frame_payload, verify; \
import serial; ser = serial.Serial('/dev/ttyACM0', 115200, timeout=0.05); \
end=time.time()+0.3
while time.time()<end: ser.read(4096)
for grp,pl,lbl in [(0x07,b'\x01','name'),(0x0F,b'\x01','sw'),(0x08,b'\x01','hw'),
    (0x08,b'\x02','hw_sub'),(0x10,b'\x00','serial0'),(0x10,b'\x01','serial1'),
    (0x05,b'\x00\x00\x00\x00','caps'),(0x06,b'','hw_id'),
    (0x04,b'\x00\x00\x00\x00','dev_type'),(0x11,b'\x04','id11')]:
    ser.reset_input_buffer(); ser.write(build_frame(grp,0x13,pl)); ser.flush()
    deadline=time.time()+0.8
    while time.time()<deadline:
        b=ser.read(1)
        if not b or b[0]!=MSG_START: continue
        n=ser.read(1); rest=ser.read(n[0]+3)
        fr=bytes([MSG_START,n[0]])+rest
        if verify(fr) and len(fr)>=4 and fr[2]==(grp|0x80):
            print(lbl, frame_payload(fr).hex()); break"
```

Routing: the `es` profile sets `wheel_device: 0x13`, which makes
`_build_identity_tables` key all wheel identity entries by `0x13`, makes the
PitHouse identity dispatch and wheel-config-echo answer from `swap_nibbles(0x13)
= 0x31`, and excludes `0x17` from the simulated-device set (heartbeats and
keepalives to `0x17` get no ACK). An early gate in `_handle_core` drops every
frame addressed to `0x17` silently — without it the replay table (built from
VGS captures) would answer `0x17` identity probes with VGS values and confuse
PitHouse. See "ES feature parity" in Pending work for what's still missing
(brightness range, LED bitmask-only writes, wake-up handling).

**PCAPNG capture only** — follow the steps below:

1. Capture a real-hardware PitHouse startup into pcapng for the wheel.
2. Extract identity bytes: run probe queries against the ndjson-filtered capture and paste the exact response bytes into a new `WHEEL_MODELS` entry. See the extraction helpers at the bottom of this README.
3. Record 7c:23 page variants from the capture: count distinct payloads (wheel→host group `0xc3`, cmd `7c:23`), paste into `_7C_23_FRAMES_<NAME>` in `wheel_sim.py`, set `_7c23_frames_name`.
4. Set `catalog_pcapng` to the capture path if PitHouse probes more than the opening description TLVs on session 2 (almost always true for wheels with integrated displays like VGS; optional for CSP-style detachable-display wheels).
5. Run `python3 sim/wheel_sim.py --model <new> /dev/ttyGS0` with PitHouse connected; iterate on any `[unhandled]` RX lines in `sim/logs/wheel_sim.log`.

## Pending work

1. **More wheel models**: add profiles to `WHEEL_MODELS` for other display-equipped wheels (FSR V2, etc.) as captures become available. Follow "Adding a new model" above — do not copy the CSP defaults blindly. Models currently supported: `vgs`, `csp`, `ks`, `kspro`, `es`.
2. **ES feature parity**: identity / plugin-probe / wheel-echo routing through `0x13` is wired (see `wheel_device` in `WHEEL_MODELS['es']`), and `0x17` traffic is dropped silently. Still missing: brightness range 0-15 enforcement, LED bitmask-only write semantics, ES wake-up sequence handling. Plugin-side ES code paths (`MozaLedDeviceManager.cs`) can be exercised today; wheel-side write semantics still echo unmodified.
3. **Dashboard upload — full pipeline landed (2026-04-24)**. Protocol negotiation + multi-round progress acks + zlib reassembly all work end-to-end. Remaining follow-up:
   - **Cold-start partial detect after PitHouse restart.** When PitHouse is relaunched while the sim is already running, PitHouse reaches `display_detected = true` but never pushes tier_def. A `sim_reload` + `sim_start` (which Windows sees as a USB drop+reattach) unblocks it every time. Likely PitHouse caches some identity-negotiation state per-wheel that becomes stale when the host process restarts; needs a capture diff between a working cold-boot (sim started first, PitHouse second) and a broken cold-boot (PitHouse started first, sim second) to pinpoint which probe we're not answering in the broken case.
   - **Dashboard name extraction — resolved 2026-04-24.** 2026-04 PitHouse omits the `/home/root/resource/dashes/...` remote path entirely; the dashboard name is parsed from the Windows-side stage path (`_dashes/<hash>/dashes/<name>/<name>.mzdash`) by `extract_mzdash_path`. Falls back to `uploaded-dashboard` only when no path of any known shape is present.
   - **176B opaque tail in dir-listing reply** (offset 45..220 of the 221B body, after `a9 88 01 00 00` magic). Entropy 6.94 bits/byte, position-9 byte varies per capture (nonce?). Not zlib at any offset/wbits mode tested. Replaying capture bytes works functionally but PitHouse may still cache-skip when it sees 11 factory dashboards listed. Also: the hardcoded reply is 221B where the real wheel emits 219B (same 1B-XOR trailer / 4B-CRC collapse as file-transfer); rebuild from raw capture chunks once more captures are available.
4. **Session 1 device→host channel catalog emission**: real wheel emits ~250 chunks (~5.3 KB) of channel URL catalog, sim only emits on session 0x02. Attempted 2026-04-24 via `session1_emits_catalog` flag; regressed session opens. Needs throttling or different chunking.
5. **Unknown application-level ACK for session 0x01 host tier-def push** (PitHouse pushes channel catalog + equalizer-gain names to wheel; sim buffers but doesn't explicitly ack the content).
6. **Active-dashboard SET-side wire signal not yet RE'd.** Sim now answers 28:00 / 28:01 dynamically based on `WheelSimulator.active_dash_index` + `active_dash_pages` (driven by the `sim_set_active_dashboard` MCP tool), but PitHouse's path for *setting* the active dashboard isn't observed in any capture — zero `3F:28` write frames anywhere in the latestcaps switch capture. Most likely candidate is an undocumented method on the session 0x0a JSON RPC channel (e.g. `useDashboard()`); sim's RPC handler logs unknown method names so the next capture session against a real wheel will harvest it. See [usb-capture/payload-09-state-re.md § Active dashboard](../usb-capture/payload-09-state-re.md).
7. **Storage display in PitHouse.** PitHouse's Dashboard Manager UI shows occupied/free space; sim currently replays the captured 14B pre-zlib metadata block in the session 0x04 dir-listing reply verbatim (4-byte LE u32 at offset 10 = 100,521 in current firmware, 273,966 in older). Hypothesis: this u32 encodes used-bytes total. Verification path: capture a fresh PitHouse session with the storage display visible and correlate the on-screen byte/KB number against that field. If confirmed, [sim/wheel_sim.py:851](wheel_sim.py#L851) `_DIR_LISTING_REPLY_TAIL` needs to compute it dynamically from `WheelFileSystem` contents.

## Analysis tools (usb-capture/)

Built 2026-04-24 to support deeper capture investigation:

| Tool | Purpose |
|------|---------|
| `usb-capture/analyze_displays.py` | Walks every `.pcapng` under `usb-capture/`, classifies each as full-handshake / mid-session / specialty, quantifies session blob counts, display cascade probes, mcUid candidates, and per-capture sim-gap (probes sim can't answer with current identity tables). Output: `usb-capture/display-negotiation-inventory.md`. |
| `usb-capture/decode_session.py` | Reassembles any session's byte stream (host or device direction) with 3-byte CRC stripping, runs envelope detection (zlib, UTF-16LE, MOZA TLV). `--session 1 --direction device` |
| `usb-capture/message_content_decoder.py` | Walks a reassembled session stream emitting semantic messages (device_desc, field0_marker, channel_entry, zlib_stream, ff-envelope). WIP — TLV size-field rules need refinement for some block types. |
| `usb-capture/sim_vs_real_diff.py` | For each host→wheel frame in a capture, compares the real wheel's response vs what sim would produce. Flags `no_handler` / `mismatch` / `extra_reply` divergences. Runs sim's `_build_identity_tables` for the chosen model to check coverage. |
| `usb-capture/analyze_session09.py` | Walks all session 0x09 device→host configJson state blobs in a capture set, reassembles (handles 0x7E destuffing, 4B CRC trailers, 9B envelope, multi-blob streams, retransmits), decompresses, and dumps each as JSON + INDEX.tsv. Used to RE schema A vs schema B and produce the per-display-module factory state files |
| `usb-capture/strip_tlv_decode.py` | Dumps `0xff`-prefix TLV blocks from a reassembled session (requires size-field format fix before it's fully useful for session 0x01 host push). |
| `usb-capture/display-investigation-plan.md` | Plan document (2026-04-24) tracking step-by-step work on display-detection gap. Open checklist items survive this session's work. |

---

## Console output mode (LLM / automation)

The default live mode is an interactive TUI that clears the screen every 100 ms. Two flags switch to non-interactive, streaming output suitable for piping, log files, or LLM consumption:

| Flag | Output |
|------|--------|
| `--console` | Structured text lines — grep-friendly, human-readable |
| `--json` | NDJSON (one JSON object per line) — programmatic consumption |

`--json` implies `--console`. The log file (`sim/logs/wheel_sim.log`) still captures raw hex frames in both modes.

### Line prefixes

Every line starts with a timestamp and one of four prefixes:

| Prefix | Meaning | Frequency |
|--------|---------|-----------|
| `EVENT` | State transition (session open, tier def, display detected, reconnect, catalog sent) | Immediate — rare, high-signal |
| `TELEM` | Decoded telemetry values | 1 Hz (throttled from 30–60 Hz raw) |
| `FRAME` | Noteworthy individual frame (first occurrence of each unhandled type) | Immediate |
| `STATE` | Full state snapshot (uptime, sessions, counters, fps) | Every 5 s |

### Text format (`--console`)

```
12:34:56.789 EVENT   session_open       sessions=1 mgmt=0x01
12:34:57.100 EVENT   tier_def           channels=14 names=Speed,RPM,Gear,Throttle,Brake,...
12:34:57.150 EVENT   display_detected   model=VGS
12:34:58.000 TELEM   values             Speed=120.3 RPM=7200 Gear=4 Throttle=0.85
12:34:58.500 FRAME   unhandled          hex=7e 06 43 17 3f 01 ... label="grp=0x43 dev=0x17 wheel LED config"
12:35:03.000 STATE   snapshot           uptime=6s sessions=2 tier_def=True display=True total=342 telem=280 fps=29.8
```

### JSON format (`--json`)

```json
{"ts": "12:34:56.789", "type": "EVENT", "tag": "session_open", "sessions": 1, "mgmt": "0x01"}
{"ts": "12:34:58.000", "type": "TELEM", "tag": "values", "Speed": 120.3, "RPM": 7200, "Gear": 4}
{"ts": "12:35:03.000", "type": "STATE", "tag": "snapshot", "uptime": "6s", "sessions": 2, "fps": 29.8}
```

### Common grep patterns

```bash
# Watch for state-change events only
python3 sim/wheel_sim.py --console /dev/tnt0 | grep EVENT

# Extract telemetry with jq
python3 sim/wheel_sim.py --json /dev/tnt0 | jq 'select(.type=="TELEM")'

# Monitor unhandled frames
python3 sim/wheel_sim.py --console /dev/tnt0 | grep "FRAME.*unhandled"
```

---

## MCP server interface (Claude Code integration)

The simulator can run as an MCP (Model Context Protocol) server, letting Claude Code query simulator state directly via tool calls instead of parsing stdout.

### Usage

```bash
# Start MCP server (does NOT auto-connect to serial port)
python3 sim/wheel_sim.py --mcp /dev/tnt0

# Port arg is optional — sets default for sim_start
python3 sim/wheel_sim.py --mcp
```

In `--mcp` mode the MCP server owns stdio (JSON-RPC transport). The simulator does **not** auto-connect — use `sim_start` to open the serial port and begin simulation. Use `sim_stop` to disconnect. A **5-second cooldown** is enforced after disconnect before reconnection is allowed.

### Available MCP tools

**Lifecycle:**

| Tool | Description |
|------|-------------|
| `sim_start` | Connect to serial port and start simulation. Accepts optional `port` override. Enforces 5s reconnect cooldown. Cross-process port lock — a second wheel_sim (another MCP server or Claude session) trying the same port returns an error with the owner PID. |
| `sim_stop` | Disconnect serial port and stop simulation threads. Starts cooldown timer. If the port is held by another wheel_sim process (no local session), signals SIGTERM / `taskkill /F` on the owner PID recorded in the lockfile so any session can stop the singular sim. |
| `sim_reload` | Reload `wheel_sim.py` from disk to pick up code edits. Stops session if running; purges the cached module so the next `sim_start` imports fresh code. MCP server process stays alive — no `/mcp` reconnect needed. |
| `sim_info` | Connection state, configured port, cooldown remaining. |

**Query (require sim running):**

| Tool | Description |
|------|-------------|
| `sim_status` | Current state: sessions, tier def, display, uptime, frame counts, fps |
| `sim_telemetry` | Decoded telemetry values (all channels or filtered by name) |
| `sim_channels` | List tier-defined channels with compression type and bit width |
| `sim_unhandled` | Unhandled frame types with counts and labels |
| `sim_recent` | Last N frames from the rolling log — supports `tag=` / `exclude=` filters |
| `sim_counters` | Per-category frame counts |
| `sim_uploads` | Decoded zlib blobs from incoming uploads (session, size, JSON root keys or UTF-16 preview) |
| `sim_stored_dashboards` | Current simulated wheel-stored dashboard list. Persisted to `sim/logs/stored_dashboards.json` across sim restarts |
| `sim_fs_tree` | Snapshot of simulated wheel filesystem (path → size/md5/mtime). Pass `path=` to restrict to a subtree |
| `sim_rpc_log` | JSON RPCs parsed from PitHouse uploads (session 0x0a). Includes dashboard delete / select / state mutations |
| `sim_reported_state` | What the sim emits to PitHouse via session 0x09 right now: configJsonList, enableManager dashboard count + names, displayVersion, rootDirPath, top-level keys, FS counts, active-dash index/pages |
| `sim_set_active_dashboard` | Track which dashboard slot the wheel "displays" — drives 28:00 (`WheelGetCfg_GetMultiFunctionSwitch`) and 28:01 (`WheelGetCfg_GetMultiFunctionNum`) replies. Accepts slot index (1–N), `dirName`, or dashboard id. Currently sim-only — PitHouse's set-side wire signal not yet RE'd ([usb-capture/payload-09-state-re.md § Active dashboard](../usb-capture/payload-09-state-re.md) tracks the open RE work) |
| `sim_push_configjson` | Re-queue a fresh session 0x09 state push on demand. `use_factory=True` (default) replays the captured factory state verbatim; `False` rebuilds from current FS + factory merge |
| `sim_reset_fs` | Wipe the virtual FS (user uploads only — factory state stays in the JSON ROM analog). Optional `install_stub=<dirName>` writes one stub mzdash for testing |

### Reconnect cooldown

After `sim_stop`, a 5-second cooldown prevents immediate reconnection. `sim_start` during cooldown returns an error with time remaining. `sim_info` reports cooldown status.

### Claude Code setup

Add to `.mcp.json` in the project root (already configured):

```json
{
  "mcpServers": {
    "wheel-sim": {
      "command": ".venv/bin/python3",
      "args": ["sim/wheel_sim.py", "--mcp", "/dev/tnt0"],
      "cwd": "/home/rorth/src/moza-simhub-plugin"
    }
  }
}
```

Requires `mcp` Python SDK: `pip install mcp` (in the project venv).

---

## Useful commands

```bash
# Build and test
dotnet build -c Release
dotnet test -c Release

# Run self-test against primary capture
python3 sim/wheel_sim.py --replay-handshake usb-capture/12-04-26/moza-startup.pcapng

# Validate telemetry decode
python3 sim/wheel_sim.py --validate usb-capture/12-04-26/moza-startup.pcapng

# Live mode as VGS (default)
python3 sim/wheel_sim.py /dev/tnt0

# Live mode as CSP
python3 sim/wheel_sim.py --model csp /dev/tnt0

# Live mode as KS (no dashboard)
python3 sim/wheel_sim.py --model ks /dev/tnt0

# Live mode as KS Pro (W18, shares W17-HW RGB display)
python3 sim/wheel_sim.py --model kspro /dev/tnt0

# Live mode as ES (old-protocol; identity proxies through base 0x13)
python3 sim/wheel_sim.py --model es /dev/tnt0

# Console output (non-interactive, grep-friendly)
python3 sim/wheel_sim.py --console /dev/tnt0

# NDJSON output (pipe to jq, LLM, etc.)
python3 sim/wheel_sim.py --json /dev/tnt0

# Live mode (Linux with tty0tty loaded)
sudo modprobe tty0tty
ln -sf /dev/tnt1 ~/.steam/steam/steamapps/compatdata/2825720939/pfx/dosdevices/com3
python3 sim/wheel_sim.py /dev/tnt0
# Launch SimHub from Steam
```

### Extracting identity bytes from a capture

Run these helpers against a real-hardware pcapng to pull the bytes you need for a new `WHEEL_MODELS` entry.

```bash
# Convert pcapng → ndjson (usb-capture/analyze_telemetry.py writes the parsed ndjson)
python3 usb-capture/analyze_telemetry.py usb-capture/<capture>.pcapng
```

```python
# FIFO-ordered probe/response pairing for 0x43/0x17 sub-device probes
import sys; sys.path.insert(0, 'sim')
from wheel_sim import extract_from_pcapng, verify, frame_payload, DEV_WHEEL, DEV_WHEEL_RSP

entries = extract_from_pcapng('usb-capture/<capture>.pcapng')
entries.sort(key=lambda x: x[1])
queue = []
seen = set()
for d, ts, f in entries:
    if not verify(f) or len(f) < 4:
        continue
    if d == 'host' and f[2] == 0x43 and f[3] == DEV_WHEEL:
        p = bytes(frame_payload(f))
        if p and p[0] not in (0x00, 0x7c, 0x7d, 0x41, 0xfc) and len(p) <= 5:
            queue.append((ts, p))
    elif d == 'device' and f[2] == 0xc3 and f[3] == DEV_WHEEL_RSP:
        p2 = bytes(frame_payload(f))
        if not p2 or p2[0] in (0x7c, 0xfc, 0x80):
            continue
        if queue:
            _, probe = queue.pop(0)
            key = probe.hex()
            if key not in seen:
                seen.add(key)
                print(f'{key:<12} → {f.hex(" ")}')
```

Use the same pattern (swap the probe filter) to extract `0x10 0x17` serials, `0x06 0x17` hw_id, etc. Paste the response-payload bytes (after group/device, before checksum) into the matching `WHEEL_MODELS` field.
