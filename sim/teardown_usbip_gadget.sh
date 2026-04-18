#!/usr/bin/env bash
# Reverse of setup_usbip_gadget.sh — unbind the gadget, remove all configfs
# entries, and stop usbipd. Safe to run multiple times.

set -u

if [[ $EUID -ne 0 ]]; then
    echo "Must run as root." >&2
    exit 1
fi

GADGET=/sys/kernel/config/usb_gadget/moza

pkill -x usbipd 2>/dev/null || true

if [[ -d "$GADGET" ]]; then
    echo "" > "$GADGET/UDC" 2>/dev/null || true
    rm -f "$GADGET/configs/c.1/acm.usb0"
    rmdir "$GADGET/configs/c.1/strings/0x409" 2>/dev/null || true
    rmdir "$GADGET/configs/c.1"               2>/dev/null || true
    rmdir "$GADGET/functions/acm.usb0"        2>/dev/null || true
    rmdir "$GADGET/strings/0x409"             2>/dev/null || true
    rmdir "$GADGET"                           2>/dev/null || true
fi

echo "[gadget removed]"
