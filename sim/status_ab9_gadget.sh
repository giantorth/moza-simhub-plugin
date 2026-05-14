#!/usr/bin/env bash
# Quick health check for the AB9 USBIP gadget pipeline. Run as root.
# Independent of the wheelbase gadget — this only checks PID 0x1000.

set -u

if [[ $EUID -ne 0 ]]; then
    echo "Must run as root." >&2
    exit 1
fi

GADGET=/sys/kernel/config/usb_gadget/moza_ab9
FAIL=0

check() {
    local status=$1 msg=$2
    printf "[%-4s] %s\n" "$status" "$msg"
    [[ "$status" == "FAIL" ]] && FAIL=1
}

# Kernel modules
if lsmod | grep -q '^dummy_hcd'; then
    check "OK" "dummy_hcd loaded"
else
    check "FAIL" "dummy_hcd not loaded"
fi

if lsmod | grep -q '^libcomposite'; then
    check "OK" "libcomposite loaded"
else
    check "FAIL" "libcomposite not loaded"
fi

# Configfs
if mountpoint -q /sys/kernel/config 2>/dev/null; then
    check "OK" "configfs mounted"
else
    check "FAIL" "configfs not mounted"
fi

if [[ -d "$GADGET" ]]; then
    check "OK" "gadget exists at $GADGET"
else
    check "FAIL" "gadget dir missing"
fi

# UDC binding
if [[ -f "$GADGET/UDC" ]]; then
    UDC_VAL=$(cat "$GADGET/UDC" 2>/dev/null)
    if [[ -n "$UDC_VAL" ]] && [[ -d "/sys/class/udc/$UDC_VAL" ]]; then
        check "OK" "UDC: $UDC_VAL"
    else
        check "FAIL" "UDC empty or invalid: '$UDC_VAL'"
    fi
else
    check "FAIL" "UDC file missing"
fi

# VID/PID
if [[ -f "$GADGET/idVendor" ]] && [[ -f "$GADGET/idProduct" ]]; then
    VID=$(cat "$GADGET/idVendor" 2>/dev/null | tr -d '[:space:]')
    PID=$(cat "$GADGET/idProduct" 2>/dev/null | tr -d '[:space:]')
    if [[ "$VID" == "0x346e" || "$VID" == "346e" ]] && [[ "$PID" == "0x1000" || "$PID" == "1000" ]]; then
        check "OK" "VID: $VID  PID: $PID"
    else
        check "FAIL" "VID: $VID  PID: $PID (expected 346e:1000)"
    fi
else
    check "FAIL" "VID/PID files missing"
fi

# Discover the ttyGS this gadget owns
TTYGS=""
PORT_NUM=$(cat "$GADGET/functions/acm.usb0/port_num" 2>/dev/null || true)
if [[ -n "$PORT_NUM" ]]; then
    TTYGS=/dev/ttyGS${PORT_NUM}
    if [[ -c "$TTYGS" ]]; then
        if [[ -r "$TTYGS" && -w "$TTYGS" ]]; then
            check "OK" "$TTYGS exists (rw)"
        else
            check "WARN" "$TTYGS exists but not rw for current user"
        fi
    else
        check "FAIL" "$TTYGS missing"
    fi
else
    check "FAIL" "could not determine ttyGS port_num for AB9 gadget"
fi

# usbipd
USBIPD_PID=$(pgrep -x usbipd 2>/dev/null || true)
if [[ -n "$USBIPD_PID" ]]; then
    check "OK" "usbipd running (PID $USBIPD_PID)"
else
    check "FAIL" "usbipd not running"
fi

if ss -tlnp 2>/dev/null | grep -q ':3240 '; then
    check "OK" "TCP 3240 listening"
else
    check "FAIL" "TCP 3240 not listening"
fi

# USBIP binding — find AB9 dummy_hcd busid (PID 0x1000)
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
    if usbip list -l 2>/dev/null | grep -q "$BUSID"; then
        check "OK" "busid $BUSID bound to usbip-host"
    else
        check "FAIL" "busid $BUSID found but not bound"
    fi

    if usbip list -r 127.0.0.1 2>/dev/null | grep -q "$BUSID"; then
        check "OK" "busid $BUSID remotely exportable"
    else
        check "FAIL" "busid $BUSID not remotely exportable"
    fi
else
    check "FAIL" "no dummy_hcd device with VID 346e PID 1000 found"
fi

# Real AB9 conflict on host bus
for d in /sys/bus/usb/devices/*-*; do
    [[ -e "$d/idVendor" ]] || continue
    real=$(readlink -f "$d" 2>/dev/null) || continue
    [[ "$real" == *dummy_hcd* ]] && continue
    if [[ "$(cat "$d/idVendor" 2>/dev/null)" == "346e" ]] \
       && [[ "$(cat "$d/idProduct" 2>/dev/null)" == "1000" ]]; then
        REAL_BUSID=$(basename "$d")
        check "WARN" "Real MOZA AB9 detected at $REAL_BUSID — may interfere on re-setup"
    fi
done

exit $FAIL
