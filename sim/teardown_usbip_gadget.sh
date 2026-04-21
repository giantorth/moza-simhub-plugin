#!/usr/bin/env bash
# Reverse of setup_usbip_gadget.sh — unbind usbip, remove configfs gadget,
# unload kernel modules. Safe to run multiple times.

set -u

if [[ $EUID -ne 0 ]]; then
    echo "Must run as root." >&2
    exit 1
fi

GADGET=/sys/kernel/config/usb_gadget/moza
FAIL=0

# Step 1: unbind from usbip while daemon is still alive.
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
if [[ -n "$BUSID" ]]; then
    usbip unbind -b "$BUSID" 2>/dev/null || true
    # Ensure all interfaces are released (CDC ACM has two).
    for iface in /sys/bus/usb/devices/"$BUSID":*; do
        iname=$(basename "$iface")
        drv=$(readlink "$iface/driver" 2>/dev/null) || continue
        echo "$iname" > "$(dirname "$drv")/unbind" 2>/dev/null || true
    done
fi

# Step 2: stop daemon (after unbind).
pkill -x usbipd 2>/dev/null || true

# Step 3: dismantle configfs gadget.
if [[ -d "$GADGET" ]]; then
    echo "" > "$GADGET/UDC" 2>/dev/null || true
    rm -f "$GADGET/configs/c.1/acm.usb0"
    rmdir "$GADGET/configs/c.1/strings/0x409" 2>/dev/null || true
    rmdir "$GADGET/configs/c.1"               2>/dev/null || true
    rmdir "$GADGET/functions/acm.usb0"        2>/dev/null || true
    rmdir "$GADGET/strings/0x409"             2>/dev/null || true
    rmdir "$GADGET"                           2>/dev/null || true
fi

# Step 4: unload kernel modules (libcomposite first, then dummy_hcd).
modprobe -r libcomposite 2>/dev/null || true
modprobe -r dummy_hcd    2>/dev/null || true

# Step 5: verify.
if ls /sys/class/udc/ 2>/dev/null | grep -qE '^dummy'; then
    echo "[WARN] dummy UDC still present in /sys/class/udc/ (module in use?)"
    FAIL=1
fi
if [[ -d "$GADGET" ]]; then
    echo "[WARN] gadget dir still exists at $GADGET"
    FAIL=1
fi

if [[ $FAIL -eq 0 ]]; then
    echo "[teardown complete — modules unloaded, gadget removed]"
else
    echo "[teardown partial — see warnings above]"
fi
