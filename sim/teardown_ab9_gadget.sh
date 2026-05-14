#!/usr/bin/env bash
# Reverse of setup_ab9_gadget.sh — unbind usbip for the AB9 BUSID, remove
# configfs gadget at /sys/kernel/config/usb_gadget/moza_ab9. Leaves the
# wheelbase gadget (PID 0x0006) and shared kernel modules alone.

set -u

if [[ $EUID -ne 0 ]]; then
    echo "Must run as root." >&2
    exit 1
fi

GADGET=/sys/kernel/config/usb_gadget/moza_ab9
FAIL=0

# Step 0: kill any ab9_sim.py holding the AB9 ttyGS. Scoped to ab9_sim only —
# do NOT touch wheel_sim or bridge here.
pkill -f 'ab9_sim\.py' 2>/dev/null || true
for _ in 1 2 3 4 5; do
    pgrep -f 'ab9_sim\.py' >/dev/null || break
    sleep 0.2
done
pkill -9 -f 'ab9_sim\.py' 2>/dev/null || true

# Determine which ttyGS belonged to the AB9 gadget before we tear it down.
TTYGS=""
if [[ -d "$GADGET" ]]; then
    PORT_NUM=$(cat "$GADGET/functions/acm.usb0/port_num" 2>/dev/null || true)
    [[ -n "$PORT_NUM" ]] && TTYGS=/dev/ttyGS${PORT_NUM}
fi

if [[ -n "$TTYGS" ]]; then
    for _ in $(seq 1 20); do
        [[ -c "$TTYGS" ]] || break
        sleep 0.1
    done
fi

# Step 1: unbind from usbip (AB9 PID 0x1000 only).
BUSID=""
for d in /sys/bus/usb/devices/*-*; do
    [[ -e "$d/idVendor" ]] || continue
    real=$(readlink -f "$d" 2>/dev/null) || continue
    if [[ "$real" == *dummy_hcd* ]] \
       && [[ "$(cat "$d/idVendor")" == "346e" ]] \
       && [[ "$(cat "$d/idProduct")" == "1000" ]]; then
        BUSID=$(basename "$d")
        break
    fi
done
if [[ -n "$BUSID" ]]; then
    usbip unbind -b "$BUSID" 2>/dev/null || true
    for iface in /sys/bus/usb/devices/"$BUSID":*; do
        iname=$(basename "$iface")
        drv=$(readlink "$iface/driver" 2>/dev/null) || continue
        echo "$iname" > "$(dirname "$drv")/unbind" 2>/dev/null || true
    done
fi

# Step 2: dismantle configfs gadget. Do NOT stop usbipd here — the wheelbase
# gadget (if running) needs it. Do NOT unload kernel modules here — same
# reason.
if [[ -d "$GADGET" ]]; then
    udc_now=$(cat "$GADGET/UDC" 2>/dev/null | tr -d '[:space:]')
    if [[ -n "$udc_now" ]]; then
        if ! timeout 10 sh -c 'echo "" > "$1/UDC"' _ "$GADGET" 2>/dev/null; then
            echo "[WARN] UDC unbind timed out after 10s — current UDC: '$udc_now'" >&2
            if [[ -n "$TTYGS" && -c "$TTYGS" ]]; then
                echo "       $TTYGS still present, openers:" >&2
                fuser -v "$TTYGS" 2>&1 | head -5 >&2 || true
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

if [[ -d "$GADGET" ]]; then
    echo "[WARN] gadget dir still exists at $GADGET"
    FAIL=1
fi

if [[ $FAIL -eq 0 ]]; then
    echo "[AB9 teardown complete — gadget removed; wheelbase gadget and modules untouched]"
    exit 0
else
    echo "[AB9 teardown partial — see warnings above]"
    exit 1
fi
