## Plugin implementation

Replicates Pithouse's observed preamble with direct session allocation.

### Startup phases

**Phase 0 — Session open + config** (~200ms–1.2s, before timer starts):
1. Send type=0x00 end markers on ports 0x01..0x10 to reclaim stale sessions (e.g. from previous SimHub crash). Without this, stale session causes fresh open to be silently ignored. Sleep 100ms.
2. Send type=0x81 session opens for 0x01 (mgmt), 0x02 (telem = `FlagByte`), 0x03 (aux, fire-and-forget). Wait up to 500ms each for fc:00 ack. Proceed with PitHouse defaults if neither acks — real wheels silently accept data on these sessions even without explicit ack. `Start()` dispatched to background thread so serial read thread stays free to deliver fc:00 acks.
3. If `TelemetryUploadDashboard` enabled, upload `.mzdash` file on **session 0x04** (device-initiated file transfer, 2025-11 firmware) via `DashboardUploader.BuildUpload()` → `TierDefinitionBuilder.ChunkMessage()`. Plugin waits for device to open session 0x04 (type=0x81), ACKs, then sends sub-msg 1 (path registration) + sub-msg 2 (file content) per [`../dashboard-upload/path-b-session-04.md`](../dashboard-upload/path-b-session-04.md). Waits up to 2s for wheel acknowledgment, then send type=0x00 end marker. 500ms sleep after END so state-refresh burst arrives before upload phase returns.
4. Send sub-message 1 preamble (`07 04 00 00 00 02 00 00 00 03 00 00 00 00`) as 7c:00 data on telemetry session — prepares wheel's tier config parser.
5. Send tier definition as 7c:00 data chunks on telemetry session (channel indices, compression codes, bit widths). **Flag bytes 0x00-based, NOT session-port-based.**
6. Send Display sub-device identity probe via 0x43.

**Phase 1 — Preamble** (~1 second, timer running):
7. Ack incoming 7c:00 channel data on telemetry session with fc:00 (session=FlagByte).
8. Send heartbeats only — no telemetry, no enable, no channel config.
9. Detect Display sub-device from 0x87 model name response.

**Phase 2 — Active** (continuous, after preamble):
10. Send `0x40` channel config burst (1E enables for pages 0-1 channels 2-5, then 28:00, 28:01, 09:00, 28:02).
11. Begin `0x41/FD:DE` enable signal (~30+ Hz).
12. Begin `0x43/7D:23` bit-packed telemetry (flags 0x00/0x01/0x02, ~30 Hz per tier).
13. Begin `0x2D/F5:31` sequence counter (~30 Hz).
14. Begin periodic streams at ~1 Hz: heartbeats, dash keepalives (0x43 to dev 0x14, 0x15, 0x17), display config (7C:27) + dashboard activate (7C:23) interleaved per page, session ack (FC:00 with session=FlagByte and current ack seq).
15. Begin `0x40/28:02` telemetry mode polling (~3 Hz).

RPM LEDs (`0x3F/1A:00`) and button LEDs (`0x3F/1A:01`) handled separately by `MozaDashLedDeviceManager` and `MozaLedDeviceManager`. Zero preamble.

**Disable → re-enable:** `Stop()` resets `FramesSent`; caller clears dispatch guard so re-enable performs full fresh startup (new port probing, new tier definition, new preamble). Required because wheel's session state may have changed while telemetry disabled.
