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

1. **`_handle_wheel()`** — hardcoded responses: session open → `fc:00` ack, display probe `0x07` → identity `0x87`, etc. Returns `None` if the frame is not a recognized wheel command.
2. **Replay table** (`ResponseReplay`) — if hardcoded handler returns None, look up the exact `(group, device, payload)` tuple in the replay table built from PCAPNG captures. Returns the observed device response.
3. **Unhandled counter** — if both miss, increment `unhandled_counts[(group, device, hex_payload)]`.

### Replay table (`ResponseReplay`)

Loaded from PCAPNG files via tshark. Pairs host→device frames with device→host frames by:
- Timestamp proximity (250ms window)
- Expected response group = `req_group | 0x80`
- Expected response device = `swap_nibbles(req_device)`

First-observed response per `(group, device, payload)` key wins; subsequent observations are discarded.

**Current replay table size**: 775 entries from `usb-capture/12-04-26/moza-startup.pcapng`.

**Self-test pass criteria**: `--replay-handshake` counts "missed replay" only when a frame whose key IS in the table fails to get a replay hit. Frames with no expected response (writes) don't count as failures — those are "orphans".

### Known replay gap: probe commands

The plugin's `ProbeMozaDevice()` in `MozaSerialConnection.cs` sends two probe frames before opening sessions:

| Probe | Group | Device | Payload |
|-------|-------|--------|---------|
| Base | 0x2B | 0x13 | `01 00 01` |
| Hub | 0x64 | 0x12 | `03 00 00` |

Neither of these is in the replay table because the capture was taken with a full device attached (the device responded before a probe cycle completed). When using the simulator under PitHouse, these probes will be unhandled. **TODO**: add synthetic responses for these two specific commands so the plugin's probe fallback succeeds.

---

## MOZA protocol quick reference

For full details: `docs/moza-protocol.md`, `usb-capture/pithouse-re.md`.

### Frame format

```
7E [N] [group] [device] [N payload bytes] [checksum]
```

Checksum: `(0x0D + sum_of_all_preceding_bytes) % 256`

Host → wheel: group=`0x43`, device=`0x17`
Wheel → host: group=`0xC3` (= `0x43 | 0x80`), device=`0x71` (nibble-swap of `0x17`)

### Session protocol

- `7C:00` type=`0x81` = session open request (host → wheel)
- `7C:00` type=`0x01` = data chunk (host → wheel)
- `fc:00 [session] [ack_lo] [ack_hi]` = session ack (wheel → host)
- Two sessions: mgmt session (FlagByte 0) + telemetry session (FlagByte != 0)

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

The captures use `usbcom.data.out_payload` (host→device) and `usbcom.data.in_payload` (device→host). tshark must extract both fields — see `extract_from_pcapng()` in `wheel_sim.py` for the exact tshark invocation.

---

## Known working state

- `--validate`: parses frames from PCAPNG, prints telemetry decode.
- `--replay-handshake` / `--replay-self-test`: 0 missed replay hits on `moza-startup.pcapng`.
- Live mode via tty0tty + Proton: known-working for SimHub plugin testing.
- USBIP gadget scripts (`setup_usbip_gadget.sh`, `teardown_usbip_gadget.sh`) written and syntax-clean; **not yet validated end-to-end against real PitHouse on Windows**.
- Hardcoded probe responses: plugin base/hub probes (`_PROBE_SYNTH`) and PitHouse VGS identity probes (`_VGS_ID_RSP`, 12 entries) live in `wheel_sim.py`.
- Plugin ack race condition: fixed in `Telemetry/TelemetrySender.cs`.

## Pending work

1. **End-to-end USBIP validation**: run `setup_usbip_gadget.sh`, attach from a Windows host via `usbip-win2`, verify PitHouse enumerates the device and completes its startup probes. Extend `_VGS_ID_RSP` for any probe that shows up `unhandled`.
2. **Dashboard display**: once PitHouse traffic is captured over USBIP, correlate PitHouse's display update commands with what the plugin currently sends and close the divergence.

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

# Live mode (Linux with tty0tty loaded)
sudo modprobe tty0tty
ln -sf /dev/tnt1 ~/.steam/steam/steamapps/compatdata/2825720939/pfx/dosdevices/com3
python3 sim/wheel_sim.py /dev/tnt0
# Launch SimHub from Steam
```
