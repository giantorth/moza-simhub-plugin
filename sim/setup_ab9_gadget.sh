#!/usr/bin/env bash
# Create a USB CDC ACM gadget impersonating a MOZA AB9 active shifter
# (VID 0x346E PID 0x1000) and export it via usbipd so a Windows host can
# attach it alongside the wheelbase gadget (see setup_usbip_gadget.sh).
#
# The two gadgets are independent: separate configfs trees, separate UDCs,
# separate usbip binds, separate ttyGS devices.  Run the AB9 sim against
# the discovered ttyGS:
#
#     python3 sim/ab9_sim.py /dev/ttyGS<N>
#
# Tear down with: sudo bash sim/teardown_ab9_gadget.sh
#
# NOTE: AB9's HID interface (1 kHz gear-state reports on EP 0x83) is NOT
# emulated — this gadget exposes CDC ACM only.  See ab9_sim.py header for
# rationale.  Add an f_hid function here later if HID gear events become
# necessary.

set -euo pipefail

if [[ $EUID -ne 0 ]]; then
    echo "Must run as root (configfs + usbipd)." >&2
    exit 1
fi

GADGET=/sys/kernel/config/usb_gadget/moza_ab9

# Clean up stale state before loading anything. Only kill stale ab9_sim
# processes — leave wheel_sim / bridge alone (they own the OTHER gadget).
if [[ -d "$GADGET" ]] \
   || pgrep -f 'ab9_sim\.py' >/dev/null 2>&1; then
    echo "Stale AB9 gadget or ab9_sim found — running AB9 teardown first..."
    if ! bash "$(dirname "$0")/teardown_ab9_gadget.sh"; then
        echo "Pre-cleanup teardown failed — refusing to proceed (gadget would EBUSY)." >&2
        echo "Inspect with: bash $(dirname "$0")/status_ab9_gadget.sh" >&2
        exit 1
    fi
fi

modprobe dummy_hcd
modprobe libcomposite

mountpoint -q /sys/kernel/config || mount -t configfs none /sys/kernel/config

mkdir -p "$GADGET"
echo 0x346E > "$GADGET/idVendor"
echo 0x1000 > "$GADGET/idProduct"
echo 0x0100 > "$GADGET/bcdDevice"
echo 0x0200 > "$GADGET/bcdUSB"
# AB9's real device descriptor declares class 0xEF (Misc) sub 0x02 proto 0x01
# (IAD-grouped composite). libcomposite tags single-config composite gadgets
# this way too, but we set it explicitly to mirror the captured descriptor.
echo 0xEF > "$GADGET/bDeviceClass"
echo 0x02 > "$GADGET/bDeviceSubClass"
echo 0x01 > "$GADGET/bDeviceProtocol"

mkdir -p "$GADGET/strings/0x409"
echo "MOZA Racing"        > "$GADGET/strings/0x409/manufacturer"
echo "AB9 Active Shifter" > "$GADGET/strings/0x409/product"
echo "AB9000000001"       > "$GADGET/strings/0x409/serialnumber"

mkdir -p "$GADGET/configs/c.1/strings/0x409"
echo "Config 1" > "$GADGET/configs/c.1/strings/0x409/configuration"
echo 100        > "$GADGET/configs/c.1/MaxPower"

mkdir -p "$GADGET/functions/acm.usb0"
ln -sf "$GADGET/functions/acm.usb0" "$GADGET/configs/c.1/"

# Pick an unused dummy UDC. The wheelbase gadget already owns one — find any
# dummy_udc.N whose UDC slot is unbound (no live gadget pointing at it).
UDC=""
for candidate in $(ls /sys/class/udc/ 2>/dev/null | grep -E '^dummy' || true); do
    # A UDC is in use if some configfs gadget has its name written to UDC.
    in_use=0
    for g in /sys/kernel/config/usb_gadget/*/UDC; do
        [[ -f "$g" ]] || continue
        if [[ "$(cat "$g" 2>/dev/null | tr -d '[:space:]')" == "$candidate" ]]; then
            in_use=1
            break
        fi
    done
    if [[ $in_use -eq 0 ]]; then
        UDC="$candidate"
        break
    fi
done
if [[ -z "$UDC" ]]; then
    echo "No free dummy UDC found. dummy_hcd default is 2 UDCs (dummy_udc.0/1)." >&2
    echo "If both are claimed, reload dummy_hcd with more slots:" >&2
    echo "    rmmod dummy_hcd && modprobe dummy_hcd num=4" >&2
    exit 1
fi
if ! timeout 5 sh -c 'echo "$1" > "$2/UDC"' _ "$UDC" "$GADGET"; then
    echo "UDC bind timed out after 5s — dummy_hcd or libcomposite likely wedged." >&2
    exit 1
fi

# Find which /dev/ttyGS<N> the kernel allocated to this function.
PORT_NUM=$(cat "$GADGET/functions/acm.usb0/port_num" 2>/dev/null || true)
if [[ -z "$PORT_NUM" ]]; then
    echo "Could not read port_num for acm.usb0 — kernel did not assign a serial port." >&2
    exit 1
fi
TTYGS=/dev/ttyGS${PORT_NUM}

for _ in 1 2 3 4 5; do
    [[ -c "$TTYGS" ]] && break
    sleep 0.2
done
chmod a+rw "$TTYGS" 2>/dev/null || true

if ! command -v usbipd >/dev/null; then
    echo "usbipd not installed — pacman -S usbip (Arch) or apt install linux-tools-generic." >&2
    exit 1
fi

# Start usbipd if it isn't already up. The wheelbase setup script may have
# started it; don't kill it here.
if ! pgrep -x usbipd >/dev/null; then
    usbipd -D
    for _ in $(seq 1 10); do
        ss -tlnp 2>/dev/null | grep -q ':3240 ' && break
        sleep 0.3
    done
    if ! ss -tlnp 2>/dev/null | grep -q ':3240 '; then
        echo "usbipd not listening on port 3240 after 3s" >&2
        exit 1
    fi
fi

# Wait for the gadget device to enumerate on the dummy_hcd host side.
BUSID=""
for attempt in $(seq 1 20); do
    for d in /sys/bus/usb/devices/*-*; do
        [[ -e "$d/idVendor" ]] || continue
        real=$(readlink -f "$d" 2>/dev/null) || continue
        if [[ "$real" == *dummy_hcd* ]] \
           && [[ "$(cat "$d/idVendor")" == "346e" ]] \
           && [[ "$(cat "$d/idProduct")" == "1000" ]]; then
            BUSID=$(basename "$d")
            break 2
        fi
    done
    sleep 0.3
done
if [[ -z "$BUSID" ]]; then
    echo "Gadget device did not enumerate on dummy_hcd after 6s." >&2
    ls -l /sys/bus/usb/devices/ >&2 || true
    dmesg | tail -20 >&2 || true
    exit 1
fi
echo "Gadget enumerated as busid $BUSID"

# Bind gadget to usbip-host. CDC ACM creates two interfaces (comms + data);
# usbip must claim both or the device won't be exportable. Same race-handling
# as setup_usbip_gadget.sh.
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
    for iface in /sys/bus/usb/devices/"$BUSID":*; do
        drv=$(readlink "$iface/driver" 2>/dev/null || echo "none")
        echo "    $(basename "$iface") → $drv" >&2
    done
    exit 1
fi

echo
echo "── AB9 gadget ready ──"
ls -l "$TTYGS"
echo "UDC:   $UDC"
echo "VID:   0x346E  PID:  0x1000"
echo "BusID: $BUSID (bound + verified exportable)"

echo
echo "Exportable devices (from this host):"
usbip list -r 127.0.0.1 || true
echo
echo "Next: python3 sim/ab9_sim.py $TTYGS"
echo "Then on Windows: usbip attach -r <linux-ip> -b $BUSID"
