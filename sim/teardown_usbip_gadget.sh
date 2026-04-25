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

# Step 0: kill any wheel_sim.py holding /dev/ttyGS0. Open fds on the gadget
# tty cause `echo "" > UDC` (step 3) to block forever waiting for hangup.
pkill -f 'wheel_sim\.py' 2>/dev/null || true
for _ in 1 2 3 4 5; do
    pgrep -f 'wheel_sim\.py' >/dev/null || break
    sleep 0.2
done
pkill -9 -f 'wheel_sim\.py' 2>/dev/null || true

# Wait for /dev/ttyGS0 to disappear — gadget driver tears it down async after
# the last close. UDC unbind in step 3 races against this cleanup if we don't
# wait, and blocks on the gadget mutex even though no openers remain.
for _ in $(seq 1 20); do
    [[ -c /dev/ttyGS0 ]] || break
    sleep 0.1
done

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

# Step 3: dismantle configfs gadget. UDC write can block if anything still
# holds /dev/ttyGS0 OR if the kernel is mid-cleanup from a recent disconnect.
# Skip the write entirely when UDC is already empty, and time out otherwise.
if [[ -d "$GADGET" ]]; then
    udc_now=$(cat "$GADGET/UDC" 2>/dev/null | tr -d '[:space:]')
    if [[ -n "$udc_now" ]]; then
        if ! timeout 10 sh -c 'echo "" > "$1/UDC"' _ "$GADGET" 2>/dev/null; then
            echo "[WARN] UDC unbind timed out after 10s — current UDC: '$udc_now'" >&2
            if [[ -c /dev/ttyGS0 ]]; then
                echo "       /dev/ttyGS0 still present, openers:" >&2
                fuser -v /dev/ttyGS0 2>&1 | head -5 >&2 || true
            else
                echo "       /dev/ttyGS0 absent — kernel likely wedged in gadget cleanup." >&2
            fi
            FAIL=1
        fi
    fi
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
    exit 0
else
    echo "[teardown partial — see warnings above]"
    exit 1
fi
