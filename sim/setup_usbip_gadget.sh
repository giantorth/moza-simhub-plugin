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

# Clean up stale state before loading anything.
if [[ -d "$GADGET" ]] || pgrep -x usbipd >/dev/null 2>&1; then
    echo "Stale gadget or usbipd found — running teardown first..."
    bash "$(dirname "$0")/teardown_usbip_gadget.sh"
fi

modprobe dummy_hcd
modprobe libcomposite

mountpoint -q /sys/kernel/config || mount -t configfs none /sys/kernel/config

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
for _ in $(seq 1 10); do
    ss -tlnp 2>/dev/null | grep -q ':3240 ' && break
    sleep 0.3
done
if ! ss -tlnp 2>/dev/null | grep -q ':3240 '; then
    echo "usbipd not listening on port 3240 after 3s" >&2
    exit 1
fi

# Wait for the gadget device to enumerate on the dummy_hcd host side.
# After writing to UDC, the gadget-side (ttyGS0) appears quickly but the
# host-side USB device (e.g. 8-1) can take longer to enumerate.
BUSID=""
for attempt in $(seq 1 20); do
    for d in /sys/bus/usb/devices/*-*; do
        [[ -e "$d/idVendor" ]] || continue
        real=$(readlink -f "$d" 2>/dev/null) || continue
        if [[ "$real" == *dummy_hcd* ]] \
           && [[ "$(cat "$d/idVendor")" == "346e" ]] \
           && [[ "$(cat "$d/idProduct")" == "0006" ]]; then
            BUSID=$(basename "$d")
            break 2
        fi
    done
    sleep 0.3
done
if [[ -z "$BUSID" ]]; then
    echo "Gadget device did not enumerate on dummy_hcd after 6s." >&2
    echo "Sysfs state:" >&2
    ls -l /sys/bus/usb/devices/ >&2 || true
    echo "dmesg tail:" >&2
    dmesg | tail -20 >&2 || true
    exit 1
fi
echo "Gadget enumerated as busid $BUSID"

# Bind gadget to usbip-host. CDC ACM creates two interfaces (comms + data);
# usbip must claim both or the device won't be exportable. If the first bind
# leaves interfaces unbound (known usbip/cdc_acm race), unbind, re-probe the
# interfaces so the kernel re-attaches drivers, then rebind.
_usbip_bind() {
    usbip bind -b "$BUSID" 2>/dev/null || true
    sleep 0.3
    if usbip list -r 127.0.0.1 2>/dev/null | grep -q "$BUSID"; then
        return 0
    fi
    echo "Bind did not export device — forcing interface re-probe..."
    usbip unbind -b "$BUSID" 2>/dev/null || true
    for iface in /sys/bus/usb/devices/"$BUSID":*; do
        echo "$(basename "$iface")" > /sys/bus/usb/drivers_probe 2>/dev/null || true
    done
    sleep 0.3
    usbip bind -b "$BUSID" 2>/dev/null || true
    sleep 0.3
    usbip list -r 127.0.0.1 2>/dev/null | grep -q "$BUSID"
}

if ! _usbip_bind; then
    echo "FATAL: gadget bound but NOT remotely exportable after retry." >&2
    echo "  Interfaces:" >&2
    for iface in /sys/bus/usb/devices/"$BUSID":*; do
        drv=$(readlink "$iface/driver" 2>/dev/null || echo "none")
        echo "    $(basename "$iface") → $drv" >&2
    done
    echo "  Debug: usbip list -r 127.0.0.1" >&2
    exit 1
fi

echo
echo "── gadget ready ──"
ls -l /dev/ttyGS0
echo "UDC:   $UDC"
echo "VID:   0x346E  PID:  0x0006"
echo "BusID: $BUSID (bound + verified exportable)"

echo
echo "Exportable devices (from this host):"
usbip list -r 127.0.0.1 || true
echo
echo "Next: python3 sim/wheel_sim.py /dev/ttyGS0"
echo "Then on Windows: usbip attach -r <linux-ip> -b $BUSID"
