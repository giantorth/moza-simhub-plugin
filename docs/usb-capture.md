# USB Traffic Capture with Wireshark

This guide explains how to capture USB traffic between your Moza device and PC. This is useful for diagnosing communication issues or helping contributors reverse-engineer device behavior.

## 1. Install Wireshark

1. Download the installer from [wireshark.org](https://www.wireshark.org/download.html)
2. During installation, check **Install USBPcap** — this is required for USB capture on Windows
3. Restart your PC after installation

---

## 2. Isolate Your Device (Recommended)

Plug your Moza device into a USB port or hub that has **no other devices connected to it**. This dramatically reduces noise from unrelated USB traffic (keyboard, mouse, audio, etc.) and makes the capture much easier to read.

**Steps:**
1. Use a dedicated USB hub, or pick a port you can use exclusively for this
2. Connect only the Moza device to that hub/port
3. Open **Device Manager** and note which USB host controller the device appears under — this helps identify which USBPcap interface to use

---

## 3. Start a Capture

1. Open **Wireshark**
2. In the interface list, you'll see entries like `USBPcap1`, `USBPcap2`, etc.
3. To find which one your device is on: briefly start a capture on each, then in the filter bar type `usb.idVendor == 0x346e` — traffic will appear on the correct interface
4. Stop, select the correct interface, and start a fresh capture

### Recommended Display Filter

Filter to only show Moza device traffic:
```
usb.idVendor == 0x346e
```

Or filter by bus and device address for a tighter view (find the device address in the first capture):
```
usb.bus_id == 1 && usb.device_address == 3
```

---

## 4. Perform Deliberate Events

Trigger one specific action at a time and note the timestamp so you can correlate packets to actions later.

**Recommended events to capture:**

| Event | What to watch for |
|---|---|
| Pithouse connects to device | First HID transfers, descriptor requests |
| Wheel button press | Short interrupt IN transfer |
| Encoder / rotary input | Interrupt IN with axis data |
| LED command sent from Pithouse | Interrupt OUT to device |
| FFB effect start / stop | Larger OUT transfers |
| Device disconnect | URB cancellations |

**Workflow:**
1. Start Wireshark capture
2. Perform **one action**
3. Note the time / add a packet comment (`Ctrl+Alt+C`) labelling what you just did
4. Repeat for additional actions
5. Stop the capture

---

## 5. Save and Share

Save as `.pcapng`: `File > Save As` (use `.pcapng`, not legacy `.pcap`).

To share only the relevant traffic, apply your display filter and then use `File > Export Specified Packets`, checking **Displayed** packets only.

---

## Tips

- **Interrupt vs bulk:** HID devices like steering wheels use interrupt transfers. Filter with `usb.transfer_type == 0x01` to see only interrupt traffic.
- **IN vs OUT:** `URB_INTERRUPT in` = device sending to host (inputs/state). `URB_INTERRUPT out` = host sending to device (commands/LEDs).
- **Payload:** HID report data appears in the `Leftover Capture Data` field (or `HID Data` if Wireshark decodes the descriptor).
- **USBPcap device filter:** When selecting the USBPcap interface in Wireshark, click the gear icon to pre-filter to a specific device — this reduces capture volume significantly.
