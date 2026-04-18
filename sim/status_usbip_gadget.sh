#!/usr/bin/env bash
# Quick health check for the USBIP gadget pipeline. Run as root.

set -u

if [[ $EUID -ne 0 ]]; then
    echo "Must run as root." >&2
    exit 1
fi

GADGET=/sys/kernel/config/usb_gadget/moza
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
    if [[ "$VID" == "0x346e" || "$VID" == "346e" ]] && [[ "$PID" == "0x0006" || "$PID" == "0006" ]]; then
        check "OK" "VID: $VID  PID: $PID"
    else
        check "FAIL" "VID: $VID  PID: $PID (expected 346e:0006)"
    fi
else
    check "FAIL" "VID/PID files missing"
fi

# ttyGS0
if [[ -c /dev/ttyGS0 ]]; then
    if [[ -r /dev/ttyGS0 && -w /dev/ttyGS0 ]]; then
        check "OK" "/dev/ttyGS0 exists (rw)"
    else
        check "WARN" "/dev/ttyGS0 exists but not rw for current user"
    fi
else
    check "FAIL" "/dev/ttyGS0 missing"
fi

# usbipd
USBIPD_PID=$(pgrep -x usbipd 2>/dev/null || true)
if [[ -n "$USBIPD_PID" ]]; then
    check "OK" "usbipd running (PID $USBIPD_PID)"
else
    check "FAIL" "usbipd not running"
fi

# TCP 3240
if ss -tlnp 2>/dev/null | grep -q ':3240 '; then
    check "OK" "TCP 3240 listening"
else
    check "FAIL" "TCP 3240 not listening"
fi

# USBIP binding — find dummy_hcd busid
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
    check "FAIL" "no dummy_hcd device with VID 346e PID 0006 found"
fi

# Real MOZA wheel conflict
for d in /sys/bus/usb/devices/*-*; do
    [[ -e "$d/idVendor" ]] || continue
    real=$(readlink -f "$d" 2>/dev/null) || continue
    [[ "$real" == *dummy_hcd* ]] && continue
    if [[ "$(cat "$d/idVendor" 2>/dev/null)" == "346e" ]] \
       && [[ "$(cat "$d/idProduct" 2>/dev/null)" == "0006" ]]; then
        REAL_BUSID=$(basename "$d")
        check "WARN" "Real MOZA wheel detected at $REAL_BUSID — may interfere on re-setup"
    fi
done

exit $FAIL
