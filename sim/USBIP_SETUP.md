# USBIP bridge — VGS wheel simulator for PitHouse

Expose the Linux wheel simulator to a Windows host as a real USB device so
PitHouse enumerates a VGS wheel (VID `0x346E` PID `0x0006`) and runs its full
probe sequence. Required because PitHouse filters devices via WMI on
`VID_346E%` — a plain virtual COM port does not qualify.

Pipeline:

```
wheel_sim.py ↔ /dev/ttyGS0 ↔ libcomposite (CDC ACM) ↔ dummy_hcd ↔ usbipd
                                                                    ↕  (TCP/3240)
                                                                 usbip-win2 → PitHouse
```

## Prerequisites

**Linux (gadget side):**

- `dummy_hcd` kernel module (stock on most Arch/Debian kernels)
- `libcomposite` kernel module (stock)
- `usbip` userspace tools
  - Arch: `sudo pacman -S usbip`
  - Debian/Ubuntu: `sudo apt install linux-tools-generic`
- Root access (configfs + usbipd)

Verify before starting:

```bash
find /lib/modules/$(uname -r) -name 'dummy_hcd*' -o -name 'libcomposite*'
command -v usbipd
```

**Windows (client side):**

- [usbip-win2](https://github.com/vadimgrn/usbip-win2) signed MSI release
- Test signing enabled or a properly signed build; install requires a reboot
- `usbip.exe` on PATH

## Linux setup

```bash
sudo bash sim/setup_usbip_gadget.sh
```

This loads modules, mounts configfs, builds the gadget at
`/sys/kernel/config/usb_gadget/moza`, binds to `dummy_udc.0`, and starts
`usbipd`. Output ends with the busid to attach from Windows (typically
`1-1`). `/dev/ttyGS0` appears with `rw` for all users.

Start the simulator:

```bash
python3 sim/wheel_sim.py /dev/ttyGS0
```

The simulator auto-loads the default VGS capture as a replay table if
`usb-capture/12-04-26-2/moza-startup-1.pcapng` exists. Identity probes are
also answered by hardcoded handlers in `_VGS_ID_RSP`, so the sim works even
without any capture file.

## Windows attach

```
usbip list -r <linux-ip>
usbip attach -r <linux-ip> -b 1-1
```

Device Manager should show a USB CDC device with `VID_346E&PID_0006`, and a
new COM port appears. PitHouse's WMI scan picks it up; point PitHouse at the
COM port or let it auto-detect.

## Capture workflow

1. Start Wireshark on Linux. Capture on the `lo` interface filtered to
   `tcp.port == 3240` (USBIP), or on the gadget's USB endpoint.
2. `python3 sim/wheel_sim.py /dev/ttyGS0`
3. Launch PitHouse on Windows.
4. Let PitHouse finish its startup probes (~3–5 s).
5. Save capture to `usb-capture/` as PCAPNG.
6. Extend the replay table:
   `python3 sim/wheel_sim.py --replay-handshake <new.pcapng>`

## Teardown

```bash
sudo bash sim/teardown_usbip_gadget.sh
```

Unbinds the gadget, removes configfs entries, stops `usbipd`. Safe to run
multiple times.

## Troubleshooting

| Symptom | Check |
|---------|-------|
| `No dummy_udc.N in /sys/class/udc/` | `modprobe dummy_hcd` succeeded? `ls /sys/class/udc/` |
| `/dev/ttyGS0` missing | `echo dummy_udc.0 > $GADGET/UDC` returned success? `dmesg \| tail` |
| `usbip list -r` from Windows hangs | Linux firewall blocking TCP 3240; `usbipd -D` running? |
| PitHouse doesn't see the device | VID wrong (`cat $GADGET/idVendor` → `0x346e`)? Driver installed (Device Manager → Ports)? |
| Sim logs `unhandled grp=0xNN dev=0x17` | PitHouse probe the sim doesn't answer; add to `_VGS_ID_RSP` or load a newer capture with `--replay-responses` |

## Reference

- `docs/SIMULATOR.md` — simulator architecture, replay table behaviour
- `docs/moza-protocol.md §Wheel connection probe sequence` — identity probe values
- `sim/wheel_sim.py` — `_PROBE_SYNTH`, `_VGS_ID_RSP` dicts for hardcoded responses
