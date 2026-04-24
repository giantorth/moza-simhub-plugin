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
7. **Replay table** (`ResponseReplay`) — exact `(group, device, payload)` lookup from PCAPNG captures. First-observed response wins.
8. **Wheel config echo** (group `0x40` to dev `0x17`) — fallback for config reads not in replay table. Echoes payload with group `0xC0`. Catches LED config queries with variable payloads (CSP pages 0-3, brightness reads, etc.).
9. **Unhandled counter** — if all above miss, increment `unhandled_counts[(group, device, hex_payload)]`.

### Replay table (`ResponseReplay`)

Loaded from PCAPNG files via tshark. Pairs host→device frames with device→host frames by:
- Timestamp proximity (250ms window)
- Expected response group = `req_group | 0x80`
- Expected response device = `swap_nibbles(req_device)`

First-observed response per `(group, device, payload)` key wins; subsequent observations are discarded.

**Current replay table size**: 775 entries from `usb-capture/12-04-26/moza-startup.pcapng`.

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

For full details: `docs/moza-protocol.md`.

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
- `fc:00 [session] [ack_lo] [ack_hi]` = session ack (response to data chunks)

**Session roles** (2025-11 firmware, verified against `usb-capture/latestcaps/automobilista2-wheel-connect-dash-change.pcapng` and prior captures):

| Session | Opened by | Role |
|---------|-----------|------|
| `0x01` | host | Management (wheel identity / log stream) |
| `0x02` | host | Telemetry (tier def + `fc:00` acks) |
| `0x03` | host | Aux config (tile-server / settings push) |
| `0x04` | device | File transfer (.mzdash upload + device → host root dir listing) |
| `0x06`, `0x08`, `0x0A` | device | Keepalive |
| `0x09` | device | configJson RPC (dashboard state) |

Sim opens its device-side sessions via `resp_device_session_open(session, port)` ~150 ms after the host finishes opening 0x01/0x02. Port byte duplicated in the open payload; the constant `FD 02` trailer is required. See `docs/moza-protocol.md` § Device-initiated session open format.

### Dashboard upload + configJson

Upload flow (session 0x04, observed from 2025-11 firmware captures):

1. Device opens session 0x04 (type=0x81).
2. Host sends sub-msg 1 (path registration TLVs, MD5, token, no content sentinel).
3. Device echoes paths back (sub-msg 1 response).
4. Host sends sub-msg 2 (path TLVs + UTF-16BE destination path + 12-byte pre-zlib header + zlib-compressed mzdash).
5. Device acks content.
6. Both sides exchange type=0x00 end markers.

Sim decodes uploaded zlib blobs via `UploadTracker`; dashboard metadata extracted from the path (`/home/moza/resource/dashes/<name>/<name>.mzdash`) and JSON body. Decoded blobs visible through `sim_uploads`.

Session 0x09 configJson is a separate RPC:
- Device pushes state JSON `{TitleId, configJsonList, disableManager, displayVersion, enableManager}` — 9-byte envelope (`flag + comp_size + uncomp_size`) + zlib stream on the first chunk.
- Host replies with compressed `{"configJson()": {"dashboards":[...], ...}, "id": 11}` list that the wheel stores in `configJsonList`.

The sim's configJson state is rebuilt from uploads in `stored_dashboards.json` (see `sim_stored_dashboards`, `sim_fs_tree`).

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
| `usb-capture/latestcaps/automobilista2-wheel-connect-dash-change.pcapng` | 2025-11 firmware wheel connect + dashboard change | Reference for session 0x04 file transfer + 2025-11 configJson schema |
| `usb-capture/latestcaps/automobilista2-dash-change.pcapng` | Warm dashboard switch (no open/close) | Reference for configJson state mutation on active dashboard change |

The captures use `usbcom.data.out_payload` (host→device) and `usbcom.data.in_payload` (device→host). tshark must extract both fields — see `extract_from_pcapng()` in `wheel_sim.py` for the exact tshark invocation.

---

## Known working state

- `--validate`: parses frames from PCAPNG, prints telemetry decode.
- `--replay-handshake` / `--replay-self-test`: 0 missed replay hits on `moza-startup.pcapng`.
- Live mode via tty0tty + Proton: known-working for SimHub plugin testing.
- USBIP gadget scripts (`setup_usbip_gadget.sh`, `teardown_usbip_gadget.sh`) written and syntax-clean; **not yet validated end-to-end against real PitHouse on Windows**.
- Hardcoded probe responses: plugin base/hub probes (`_PROBE_SYNTH`) and PitHouse identity probes (`_PITHOUSE_ID_RSP`, built from `--model` selection) live in `wheel_sim.py`.
- Multi-model support: `--model vgs` (default), `--model csp`, or `--model ks`. Identity strings, capability flags, and hardware IDs are derived from the selected model profile (`WHEEL_MODELS` dict). CSP identity extracted from `usb-capture/CSP captures/pithouse-complete.txt`. VGS identity + sub-device values + session-1/2 replay extracted from `usb-capture/connect-wheel-start-game.pcapng`. KS identity captured live from real R5 base + KS wheel via `sim/probe_wheel.py` (2026-04-20) — no dashboard, no display sub-device.
- Plugin ack race condition: fixed in `Telemetry/TelemetrySender.cs`.
- Device-initiated session opens: sim proactively opens sessions 0x04/0x06/0x08/0x09/0x0A 150 ms after host brings up 0x01/0x02 — required for PitHouse's dashboard UI (session 0x09 populates `configJsonList`) and for the plugin's session 0x04 upload path.
- Dashboard upload decode: `UploadTracker` reassembles FF-prefixed chunks, decompresses zlib, parses mzdash JSON + path. Uploads are persisted to `sim/logs/stored_dashboards.json` and surface through `sim_uploads` / `sim_stored_dashboards`.
- configJson schemas: sim emits and parses both the 2026-04 (`disabledManager`/`updateDashboards`) and 2025-11 (`disableManager`/`dashboards`/`configJsonList`/`displayVersion`) variants. Active schema is chosen from the most recent observed state.

---

## Wheel model profiles (`WHEEL_MODELS`)

Each profile in `sim/wheel_sim.py` declares the bytes the sim emits for one wheel. PitHouse is strict: mismatches on any of the fields below cause either total mis-identification or "partially detected" states (e.g. wheel correct but display empty in the dashboard management tab).

### Identity fields — match real hardware exactly

| Field | Probe | Response group | Extract from pcapng |
|-------|-------|----------------|---------------------|
| `name` | `0x07 0x17 [01]` | `0x87` | 16-byte ASCII, null-padded |
| `sw_version` | `0x0f 0x17 [01]` | `0x8f` | 16-byte ASCII |
| `hw_version` | `0x08 0x17 [01]` | `0x88` | 16-byte ASCII |
| `hw_sub` | `0x08 0x17 [02]` | `0x88` | 16-byte ASCII |
| `serial0` | `0x10 0x17 [00]` | `0x90` | 16-byte ASCII — **must be real serial, placeholders break detection** |
| `serial1` | `0x10 0x17 [01]` | `0x90` | 16-byte ASCII |
| `caps` | `0x05 0x17 [00 00 00 00]` | `0x85` | 4 bytes — bit `0x20` of byte 2 advertises a detachable RGB display (CSP); without it (VGS/KS) PitHouse skips the sub-device probe cascade |
| `hw_id` | `0x06 0x17` | `0x86` | Variable-length bytes |
| `dev_type` (optional) | `0x04 0x17 [00 00 00 00]` | `0x84` | 4 bytes. Defaults to `01:02:04:06` (VGS/CSP). KS real HW returns `01:02:05:06` — set explicitly on new profiles if real HW differs |
| `identity_11` (optional) | `0x11 0x17 [04]` | `0x91` | Defaults to `04:01` (VGS/CSP). Real VGS/CSP both return this; `00:00` makes PitHouse mis-identify VGS. KS real HW returns `04:00` — set explicitly on new profiles if real HW differs. CSP tolerates the wrong value via the caps-bit 0x20 fallback path |

### Display sub-device (nested `display` dict)

Sent under `(group=0x43, device=0x17)` when SimHub plugin's `SendDisplayProbe` fires. PitHouse also issues the cascade when caps bit `0x20` is set.

- `name`, `sw_version`, `hw_version`, `hw_sub`, `serial0`, `serial1`, `dev_type`, `caps`, `hw_id` — must be per-model real-hardware values, **not** a copy of the wheel's own `hw_id` or placeholder strings. A placeholder display `hw_id` was enough to make PitHouse report VGS as a wrong-model wheel.

### Proactive wheel-initiated frames

- `emits_7c23` (bool) + `_7c23_frames_name` (`"VGS"` | `"CSP"`): when True, the sim emits the model's dashboard-activate page frames at startup and a ~1 Hz periodic cycle after catalog upload. Byte 2 after `7c 23` differs per wheel (VGS page 1 = `0x32`, CSP page 1 = `0x3c`); VGS emits 3 page variants, CSP emits 2. Using the wrong set makes PitHouse mis-enumerate dashboard pages and fail to "fully detect" the display. Set `False` for wheels without a dashboard screen (KS); passive capture against real KS showed zero `7c:23` frames.
- `session_layout` (`"legacy"` | `"vgs_combined"`): controls `build_device_catalog`:
  - `"legacy"` (default, CSP): session 1 carries the device description, session 2 carries the channel URL catalog.
  - `"vgs_combined"` (VGS): session 1 carries the tiny seed (`ff` + a 9-byte control TLV); session 2 carries the device description TLVs (split at real-HW boundaries 26/5/2/9/2 bytes, each chunk with its own CRC-32) followed by the channel catalog.
- `catalog_pcapng` (string, optional): path (relative to repo root) to a real-hardware capture. When set, the sim replays session 1 and 2 wheel→host frames byte-for-byte from the capture instead of synthesizing via `build_device_catalog`. Synth only covers the opening description TLVs; real VGS sends ~150 follow-up TLVs on session 2 that PitHouse waits for before sending the full tier definition. VGS uses `usb-capture/connect-wheel-start-game.pcapng`; CSP falls back to synth (works because PitHouse's CSP path doesn't need the extended session 2 TLVs). Replayed chunk seqs are shifted at send time to match PitHouse's current port counter (see next item).
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

**PCAPNG capture only** — follow the steps below:

1. Capture a real-hardware PitHouse startup into pcapng for the wheel.
2. Extract identity bytes: run probe queries against the ndjson-filtered capture and paste the exact response bytes into a new `WHEEL_MODELS` entry. See the extraction helpers at the bottom of this README.
3. Record 7c:23 page variants from the capture: count distinct payloads (wheel→host group `0xc3`, cmd `7c:23`), paste into `_7C_23_FRAMES_<NAME>` in `wheel_sim.py`, set `_7c23_frames_name`.
4. Set `catalog_pcapng` to the capture path if PitHouse probes more than the opening description TLVs on session 2 (almost always true for wheels with integrated displays like VGS; optional for CSP-style detachable-display wheels).
5. Run `python3 sim/wheel_sim.py --model <new> /dev/ttyGS0` with PitHouse connected; iterate on any `[unhandled]` RX lines in `sim/logs/wheel_sim.log`.

## Pending work

1. **More wheel models**: add profiles to `WHEEL_MODELS` for other display-equipped wheels (KS Pro/W18, FSR V2, etc.) as captures become available. Follow "Adding a new model" above — do not copy the CSP defaults blindly. Models currently supported: `vgs`, `csp`, `ks`.

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
| `sim_start` | Connect to serial port and start simulation. Accepts optional `port` override. Enforces 5s reconnect cooldown. |
| `sim_stop` | Disconnect serial port and stop simulation threads. Starts cooldown timer. |
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
