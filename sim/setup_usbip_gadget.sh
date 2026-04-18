#!/usr/bin/env bash
# Create a USB CDC ACM gadget impersonating a MOZA VGS wheel (VID 0x346E
# PID 0x0006) and export it via usbipd so a Windows host can attach it.
# After this completes, run the simulator on the gadget's ACM port:
#
#     python3 sim/wheel_sim.py /dev/ttyGS0
#
# On Windows: usbip attach -r <linux-ip> -b 1-1
#
# Tear down with: sudo bash sim/teardown_usbip_gadget.sh

set -euo pipefail

if [[ $EUID -ne 0 ]]; then
    echo "Must run as root (configfs + usbipd)." >&2
    exit 1
fi

GADGET=/sys/kernel/config/usb_gadget/moza

modprobe dummy_hcd
modprobe libcomposite

mountpoint -q /sys/kernel/config || mount -t configfs none /sys/kernel/config

if [[ -d "$GADGET" ]]; then
    echo "Gadget already exists at $GADGET — run teardown first." >&2
    exit 1
fi

mkdir -p "$GADGET"
echo 0x346E > "$GADGET/idVendor"
echo 0x0006 > "$GADGET/idProduct"
echo 0x0300 > "$GADGET/bcdDevice"
echo 0x0200 > "$GADGET/bcdUSB"

mkdir -p "$GADGET/strings/0x409"
echo "MOZA Racing"  > "$GADGET/strings/0x409/manufacturer"
echo "VGS Wheel"    > "$GADGET/strings/0x409/product"
echo "VGS000000001" > "$GADGET/strings/0x409/serialnumber"

mkdir -p "$GADGET/configs/c.1/strings/0x409"
echo "Config 1" > "$GADGET/configs/c.1/strings/0x409/configuration"
echo 250        > "$GADGET/configs/c.1/MaxPower"

mkdir -p "$GADGET/functions/acm.usb0"
ln -sf "$GADGET/functions/acm.usb0" "$GADGET/configs/c.1/"

UDC=$(ls /sys/class/udc/ 2>/dev/null | grep -E '^dummy' | head -1 || true)
if [[ -z "$UDC" ]]; then
    echo "No dummy_udc.N in /sys/class/udc/ — is dummy_hcd loaded?" >&2
    exit 1
fi
echo "$UDC" > "$GADGET/UDC"

# Wait for /dev/ttyGS0 to appear, then relax permissions.
for _ in 1 2 3 4 5; do
    [[ -c /dev/ttyGS0 ]] && break
    sleep 0.2
done
chmod a+rw /dev/ttyGS0 2>/dev/null || true

if ! command -v usbipd >/dev/null; then
    echo "usbipd not installed — pacman -S usbip (Arch) or apt install linux-tools-generic." >&2
    exit 1
fi

pkill -x usbipd 2>/dev/null || true
usbipd -D
sleep 0.5

# Bind the gadget so remote hosts can see it via `usbip list -r <ip>`.
# Must pick the busid whose sysfs path lives under dummy_hcd — a plain
# `usbip list -l | awk` would grab the first USB device on the system
# (often a random real peripheral). Also required because a real MOZA
# wheel plugged into this machine shares VID:PID 346e:0006.
BUSID=""
for d in /sys/bus/usb/devices/*-*; do
    [[ -e "$d/idVendor" ]] || continue
    real=$(readlink -f "$d" 2>/dev/null) || continue
    if [[ "$real" == *dummy_hcd* ]] \
       && [[ "$(cat "$d/idVendor")" == "346e" ]] \
       && [[ "$(cat "$d/idProduct")" == "0006" ]]; then
        BUSID=$(basename "$d")
        break
    fi
done
if [[ -z "$BUSID" ]]; then
    echo "Could not find gadget busid under dummy_hcd. Sysfs state:" >&2
    ls -l /sys/bus/usb/devices/ >&2 || true
    exit 1
fi
usbip bind -b "$BUSID"

echo
echo "── gadget ready ──"
ls -l /dev/ttyGS0
echo "UDC:   $UDC"
echo "VID:   0x346E  PID:  0x0006"
echo "BusID: $BUSID (bound)"
echo
echo "Exportable devices (from this host):"
usbip list -r 127.0.0.1 || true
echo
echo "Next: python3 sim/wheel_sim.py /dev/ttyGS0"
echo "Then on Windows: usbip attach -r <linux-ip> -b $BUSID"
