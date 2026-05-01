## Tier definition protocol (group 0x43, session data on 7c:00)

Tier configuration uses TLV (tag-length-value) encoding exchanged as 7c:00 session data chunks. **Two-way handshake**: wheel declares channel catalog, host tells wheel how to decode incoming telemetry.

### Handshake sequence (from bidirectional frame traces)

Before Pithouse opens sessions, wheel already advertises channel catalog via `7c:23` display config frames. Full handshake traced frame-by-frame from VGS (`moza-startup-1.pcapng`) and CSP (`pithouse-complete.txt`):

```
Phase 1 — Wheel advertisement (before session opens):
  Wheel sends 7c:23 display config frames at ~10Hz (alternating payloads)

Phase 2 — Session open + wheel channel catalog:
  Host  >>> 7C:00 SESSION_OPEN port=0x01, port=0x02 (same USB packet)
  Wheel <<< FC:00 ACK for both sessions (immediate)
  Wheel <<< 7C:00 session 0x01: tag 0x07 (version=0) + tag 0x0c (device hash)
                                + tag 0x01 + tag 0x05 + tag 0x04 ch=0 + tag 0x06 END
  Wheel <<< 7C:00 session 0x02: tag 0xff (sentinel) + tag 0x03 (value=1)
                                + tag 0x04 × N channel URLs + tag 0x06 END (total=N)
  Host  >>> FC:00 ACKs for wheel's channel data (incremental)

Phase 3 — Host tier config (format depends on wheel model):
  Host  >>> 7C:00 session 0x02: tier definition (version 0 = CSP, see [`version-0-url-csp.md`](version-0-url-csp.md); version 2 = VGS/CS, see [`version-2-compact-vgs.md`](version-2-compact-vgs.md))
  Host  >>> FC:00 ACKs continue for any remaining wheel data

Phase 4 — Telemetry starts:
  Host  >>> 7D:23 telemetry frames (~30 Hz)
  Host  >>> FD:DE enable signal (~30 Hz, starts ~1s after session open)

Phase 5 — Channel config burst (~1s after session open):
  Host  >>> 0x40 1E:xx channel enables, 28:00, 28:01, 09:00, 28:02
  Host  >>> Second batch of tier definitions (real dashboard tiers at higher flags)
```

Both VGS and CSP follow this sequence. Wheel always declares version 0 (`tag 0x07 param=1 value=0x00`) — both models send identical version tags. Pithouse decides host→wheel response format based on wheel's model name (from 0x87 identity response), not from version tag.

**Timing note:** On VGS, Pithouse starts telemetry (flag=0x00, 11B probe tier) at t+0.3s after session open, BEFORE enable signal or channel config. Enable starts at t+1.0s. Real dashboard telemetry (flag=0x03, 16B) starts at t+1.5s after second tier definition batch.

### In-game dashboard switch (post-startup)

When the user switches dashboards via PitHouse during gameplay, the sequence is lighter than the full startup handshake:

```
Phase 1 — FF-record switch:
  Host  >>> session 0x02: ff 0c 00 00 00 [crc] [field1=4] [slot] [0] [crc]

Phase 2 — Wheel re-pushes catalog (~200-500ms):
  Wheel >>> session 0x01: tag 0x04 channel URLs (\x01-prefix shorthand)

Phase 3 — Tier-def re-send (~800ms after FF-record):
  Host  >>> session 0x01: tag 0x00 enables + tag 0x01 tier defs + tag 0x06 end
  (NO tag 0x07/0x03 preamble — preamble only on initial session connect)

Phase 4 — Channel config burst:
  Host  >>> 0x40 1E:xx channel enables, 28:00, 28:01, 28:02
```

**No session close/reopen.** Sessions 0x01-0x03 stay open throughout. No LVGL re-upload, no display probe. See [`../findings/2026-04-30-dashboard-switch-3f27.md`](../findings/2026-04-30-dashboard-switch-3f27.md) for FF-record wire format and slot indexing.

**Implementation note (2026-05-01):** The host must begin buffering catalog data on session 0x01 **immediately after sending the FF-record** (Phase 1), not after the ~800ms delay. The wheel's catalog push (Phase 2) completes well before the 800ms tier-def re-send. If the host only opens its buffer when building the tier-def, the catalog chunks have already been delivered and discarded, causing the tier-def to use stale channel indices from the previous dashboard.
