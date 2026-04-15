# Project Overview

SimHub plugin for MOZA Racing hardware providing two-way telemetry: streams game data (speed, RPM, gear, lap times, fuel, tyre wear, etc.) to the wheel dashboard display, drives wheel/dashboard RPM and flag LEDs, and allows configuring wheelbase settings. Uses a custom binary serial protocol reverse-engineered from the [boxflat](https://github.com/Lawstorant/boxflat) project.

## Building from Source

The project targets .NET Framework 4.8 (x86) and uses the `Microsoft.NETFramework.ReferenceAssemblies.net48` NuGet package so it can cross-compile on Linux without Mono. The built DLL runs on Windows under SimHub.

### Building on Windows

#### Prerequisites

- [VS Code](https://code.visualstudio.com/) with the [C# Dev Kit](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csdevkit) extension
- .NET SDK 8.0+ ([download](https://dotnet.microsoft.com/download))

#### Steps

1. **Open the project folder** in VS Code.

2. **Build** from the VS Code terminal:

   ```
   dotnet build -c Release
   ```

3. **Copy the DLL** to your SimHub folder:

   Copy `bin/x86/Release/MozaPlugin.dll` into your SimHub installation directory.

   Or set the `SIMHUB_PATH` environment variable to have it copied automatically on build:

   ```
   set SIMHUB_PATH=C:\Program Files (x86)\SimHub
   dotnet build -c Release
   ```

   PowerShell:
   ```powershell
   $env:SIMHUB_PATH = "C:\Program Files (x86)\SimHub"
   dotnet build -c Release
   ```

4. **Restart SimHub.** The plugin appears under Settings > Plugins as "MOZA Control".

### Cross-Compiling on Linux

You can build the plugin entirely on Linux. The .NET SDK can target .NET Framework 4.8 using the `Microsoft.NETFramework.ReferenceAssemblies.net48` NuGet package (already included in the `.csproj`).

#### Prerequisites

- .NET SDK 8.0+ (`dotnet-sdk` package from your distro or [Microsoft repos](https://dotnet.microsoft.com/download))

#### Steps

1. **Install the .NET SDK** if you don't have it:

   ```bash
   # Arch Linux
   sudo pacman -S dotnet-sdk

   # Ubuntu/Debian
   sudo apt install dotnet-sdk-8.0

   # Fedora
   sudo dnf install dotnet-sdk-8.0
   ```

2. **Build:**

   ```bash
   dotnet build -c Release
   ```

   The output DLL will be in `bin/x86/Release/MozaPlugin.dll`.

3. **Copy the built DLL to your Windows SimHub installation:**

   ```bash
   # Example: copy to a Windows machine via scp
   scp bin/x86/Release/MozaPlugin.dll user@windows-pc:"C:/Program Files (x86)/SimHub/"

   # Or copy to a shared folder, USB drive, etc.
   cp bin/x86/Release/MozaPlugin.dll /mnt/shared/SimHub/
   ```

4. **Restart SimHub** on Windows.

#### Notes

- The `Microsoft.NETFramework.ReferenceAssemblies.net48` NuGet package provides the .NET Framework 4.8 reference assemblies, so you do **not** need Mono or Windows installed.
- The build produces a standard .NET Framework DLL that runs natively on Windows under SimHub.
- SimHub DLLs in `libs/SimHub/` are reference-only (`Private=false`) and not copied to output.
- The build produces a single output DLL with no additional runtime dependencies to deploy.

### Running Tests

The test project (`MozaPlugin.Tests/`) uses xUnit and targets `net9.0`. It includes the testable source files directly from the main project (via `<Compile Include>`) rather than using a `<ProjectReference>`, because the main project's net48 DLL has WPF and SimHub dependencies that cannot load under .NET 9 on Linux. Minimal stubs in `MozaPlugin.Tests/Stubs/` satisfy the two external types referenced by included source files (`GameReaderCommon.StatusDataBase` and `SimHub.Logging.Current`).

```bash
dotnet test -c Release
```

This works on both Linux and Windows.

**Test structure:**

| Layer | Test file | Coverage |
|---|---|---|
| Protocol utilities | `Protocol/MozaProtocolTests.cs` | Checksum, nibble swap, bit toggle, captured frame validation |
| Command building | `Protocol/MozaCommandTests.cs` | Read/write message construction, big-endian encoding, parse round-trips |
| Response parsing | `Protocol/MozaResponseParserTests.cs` | Known command parsing, noise filtering, null handling |
| Bit packing | `Telemetry/TelemetryBitWriterTests.cs` | LSB-first writes, cross-byte spans, float/double, external buffers |
| Value encoding | `Telemetry/TelemetryEncoderTests.cs` | All 25+ compression types with clamping and edge cases |
| Frame assembly | `Telemetry/TelemetryFrameBuilderTests.cs` | Header format, checksum, stub frames, flag byte patching |
| Tier definitions | `Telemetry/TierDefinitionBuilderTests.cs` | CRC-32, chunking, tier structure, channel indices, probe batch |
| Dashboard upload | `Telemetry/DashboardUploaderTests.cs` | Zlib round-trip, Adler-32, MD5, FF-prefixed field structure |
| Test patterns | `Telemetry/TelemetryDiagnosticsTests.cs` | Determinism, known values, DRS toggling, frame log capping |
| Capture verification | `Integration/CaptureComparisonTests.cs` | Captured frame checksum, header match, builder output comparison |

The `Integration/F1DashboardProfileFixture.cs` shared helper manually constructs the F1 dashboard profile (9 channels, 126 bits, 16 bytes) without depending on `DashboardProfileStore`, which requires SimHub DLLs at runtime.

### CI/CD

- **Build**: Every push to `main` and every PR is built automatically via GitHub Actions.
- **Release**: Pushing a `v*` tag (e.g., `v0.2.0`) builds a Release, generates a changelog, and publishes a GitHub Release with the DLL (device definitions are embedded in the DLL).
- **SimHub dependency updates**: A daily workflow checks for new SimHub releases and creates a PR to update `libs/SimHub/`.

## Architecture

### Component Layers

**Plugin Entry Point** (`MozaPlugin.cs`) — Implements SimHub's `IPlugin`, `IDataPlugin`, `IWPFSettingsV2`. Manages lifecycle (Init/DataUpdate/End), connection state, auto-reconnect (5s timer), setting polling (2s timer), `TelemetrySender` for dashboard data streaming, and `DashboardProfileStore` for profile management. Uses phased per-device initialization: `TryConnect()` sends only lightweight detection probes, then `DetectDevices()` applies saved settings and reads back device state as each device (base, wheel, dashboard, handbrake, pedals) is confirmed present. This is the orchestrator that wires everything together.

**Serial Protocol Layer** (`Protocol/`) — Binary protocol over USB serial at 115200 baud (VID `0x346E`):
- `MozaSerialConnection` — Device auto-discovery, background read/write threads, frame assembly
- `MozaCommand` — Message builder with big-endian int encoding
- `MozaCommandDatabase` — 150+ pre-built command definitions
- `MozaResponseParser` — Decodes responses using bit-7 toggle, nibble swap, and wildcard matching
- `MozaProtocol` — Constants (start byte `0x7E`, device IDs, checksum formula: `(13 + sum) % 256`)
- `MozaHidReader` — Reads physical input positions (steering, throttle, brake, clutch, paddles, handbrake) from Moza HID devices using HidSharp. Enumerates devices by name regex (matching boxflat patterns), opens per-device receiver threads, and writes normalized values to `MozaData`. Independent of the serial protocol — provides live axis data for UI display at 30 Hz

**Device Management** (`MozaDeviceManager.cs`) — High-level read/write API. Handles wheel device ID cycling (IDs 23→21→19) since ES wheels respond on different IDs. `ResetWheelDetection()` must be called on disconnect so detection probes are re-sent on reconnect. `ReadSettingsPaced()` runs a batch of reads on a background task with an extra ~10ms gap between enqueues — used for large startup bursts (30+ wheel setting reads) that the wheel would otherwise drop, without throttling steady-state telemetry.

**Data Model** (`Telemetry/MozaData.cs`) — Thread-safe storage (~60 volatile fields, including string identity fields) for all device values including base, wheel, dashboard, handbrake, pedals, and HID physical input positions (steering angle, pedal axes, paddle axes, handbrake). `UpdateFromCommand()` maps parsed responses to fields; `UpdateFromArray()` handles color/timing byte arrays with per-branch length checks (colors require 3+ bytes, identity strings accept any length).

**Dashboard Telemetry Streaming** (`Telemetry/`) — Streams game data to the wheel dashboard display via a multi-tier binary protocol:
- `TelemetrySender` — Timer-based sender with three-phase startup matching Pithouse's observed preamble: (0) **port probe + config** — sends type=0x81 session opens from port 1 upward, waits ~80ms for fc:00 acks, skips ports consumed by SimHub's built-in support or Pithouse; sends tier definition on the telemetry session (format depends on `ProtocolVersion`: version 2 sends sub-message 1 preamble + compact numeric tier defs via `TierDefinitionBuilder`, version 0 sends URL subscription via `BuildV0UrlSubscription`); probes Display sub-device via 0x43 identity commands. (1) **preamble** (~1s) — acks incoming 7c:00 channel data with fc:00 and buffers the payload for channel catalog parsing, sends heartbeats only, detects Display from 0x87 response. On preamble completion: parses the wheel's channel catalog (tag 0x04 URLs) from the buffered session data and stores it in `WheelChannelCatalog` for UI display; for version 0, re-sends the URL subscription (PitHouse double-sends). (2) **active** — sends `0x40` channel config burst (channel enables, dashboard state queries 28:00/28:01, mode set 28:02), begins `0x41` enable signal (~30 Hz), `7d:23` telemetry per tier (30ms/500ms/2000ms), `0x2D` sequence counter, mode frames, dash/wheel keepalives (0x43 to 0x14/0x15/0x17), display config, session acks. Two configurable settings: `ProtocolVersion` (0=URL-based, 2=compact numeric) and `FlagByteMode` (0=zero-based, 1=session-port, 2=two-batch; version 2 only). Telemetry deferred until wheel is detected (`StartTelemetryIfReady`), which dispatches `Start()` to a background thread so the serial read thread stays free to deliver fc:00 ack responses during port probing. `Start()` checks `_enabled` between phases so a concurrent `Stop()` exits cleanly without creating orphaned timers. `Stop()` resets `FramesSent` and the caller resets the dispatch guard so disable→re-enable performs a full fresh startup with new port probing. Exposes `DisplayDetected`/`DisplayModelName` for UI gating and `WheelChannelCatalog` for channel display. Supports test patterns for diagnostics
- `TelemetryFrameBuilder` — Assembles complete Moza telemetry frames (`7E [N] 43 17 7D 23 ... [flag] 20 [data] [checksum]`) by bit-packing channel values from a `GameDataSnapshot`. Flag byte is `TierFlagBase + tierIndex`, where the base depends on `FlagByteMode`. We send 28:00/28:01 dashboard state queries during channel config but don't yet read the responses — we could use them to auto-detect which dashboard is loaded on the wheel
- `TelemetryEncoder` — Encodes game values to compressed wire formats (25+ types: `float_001`, `percent_1`, `tyre_temp_1`, `int30`, etc.) with appropriate scaling and clamping
- `TelemetryBitWriter` — LSB-first bit packer that writes variable-width values into a byte buffer
- `GameDataSnapshot` — Flat struct populated from SimHub's `StatusDataBase` API, decoupling frame building from SimHub types. Normalizes units (e.g. throttle 0–100 → 0.0–1.0)
- `DashboardProfile` / `MultiStreamProfile` — Channel definitions grouped into tiers by update frequency. Each channel maps a Moza telemetry URL to a `SimHubField` enum, compression type, and bit width
- `DashboardProfileStore` — Parses `.mzdash` dashboard files (extracts `Telemetry.get()` URLs via regex), loads channel metadata from embedded `Data/Telemetry.json`, and maps URL suffixes to SimHub fields via a static `UrlFieldMap`
- `TierDefinitionBuilder` — Builds tier definition messages in two formats. `BuildTierDefinitionMessage()` generates version 2 (compact numeric): flag bytes, channel indices, compression codes, and bit widths per tier. `BuildV0UrlSubscription()` generates version 0 (URL-based): sentinel + config + tag 0x04 channel URLs + end marker — the wheel firmware resolves compression internally. Also provides `BuildProbeBatch()` for FlagByteMode=2. CRC-32 (ISO 3309) on ALL chunks including final (verified against captures). `ChunkMessage()` and `Crc32()` shared between both versions. Version 2 compression codes confirmed from capture: 0x00=bool, 0x04=uint16_t, 0x07=float, 0x0D=int30, 0x0E=percent_1, 0x0F=float_6000_1, 0x14=uint3, 0x17=float_001
- `TelemetryDiagnostics` — Frame logging and test pattern generation

**UI** (`UI/`) — WPF plugin settings panel with tabs for Base, Wheel, Handbrake, Pedals, Options, and Telemetry diagnostics. Dashboard LED settings and wheel LED settings live on their respective device pages (Devices section), not in the plugin panel. The Base, Wheel, Pedals, and Handbrake tabs each have a "Live Input" section showing real-time axis positions via HID (30 Hz DispatcherTimer at Render priority). The Wheel tab shows paddle position bars and paddle mode/clutch split point settings. Uses `_suppressEvents` flag during 500ms refresh timer to prevent feedback loops. Shows a restart banner when new device definitions are deployed at runtime.

**Profile System** (`UI/MozaProfile.cs`, `UI/MozaProfileStore.cs`) — Per-game configuration snapshots using SimHub's `ProfileBase`. Uses -1 sentinel to mark settings not included in a profile.

**Device Extension System** (`Devices/`) — Registers MOZA devices as SimHub devices so they appear in SimHub's Devices section with native LED effects support. Each device type (wheel, dashboard) has its own extension, LED manager, settings class, and settings control:

*Shared:*
- `MozaDeviceExtensionFilter` — `IDeviceExtensionFilter` that routes devices by `DescriptorUniqueId` GUID to the correct extension. Uses `MozaDeviceConstants.GetWheelModelPrefix()` to match all known wheel GUIDs → `MozaWheelDeviceExtension`, and `IsDashDevice()` to match the dashboard GUID → `MozaDashDeviceExtension`
- `MozaDeviceConstants` — Per-model `DescriptorUniqueId` GUIDs, GUID-to-model-prefix mapping, and LED count constants. `GetWheelModelPrefix()` resolves a SimHub `DeviceTypeID` to the firmware model prefix the device expects
- `WheelModelInfo` — Per-model LED layout descriptor (button count, flag presence, button index remapping). Resolved from the firmware model name string after wheel detection. Known models: GS V2P (10 buttons, no flags), CS V2.1 (6 non-contiguous buttons), CSP/KSP/FSR2 (14 buttons, flags). Unknown models default to 14 buttons, no flags

*Wheel:*
- `MozaWheelDeviceExtension` — `DeviceExtension` subclass providing a settings tab and per-game device profiles via `GetSettings()`/`SetSettings()`. Resolves the expected model prefix from `DeviceTypeID` in `Init()` and passes it to the LED driver for model-aware connection binding
- `MozaLedDeviceManager` — Virtual `ILedDeviceManager` injected via reflection into the device's LED module. `IsConnected()` is model-aware: old-protocol devices match only `IsOldWheelDetected`, per-model devices match by firmware model name prefix, generic devices match any new-protocol wheel (unless a model-specific extension is active — tracked via copy-on-write `_activeModelPrefixes` set for thread safety). Fires `OnConnect`/`OnDisconnect` events from `UpdateConnectionState()`. Forwards computed `Color[]` to MOZA hardware as per-frame color chunks + bitmask in `Display()`. Button LED count and index mapping are model-aware via `WheelModelInfo` — non-contiguous layouts (e.g. CS V2.1) are remapped from SimHub's contiguous indices to the correct protocol positions
- `MozaWheelExtensionSettings` — Wheel-specific settings serialized to SimHub device profiles
- `MozaWheelSettingsControl` — Status panel with indicator modes, brightness, color swatches, and input settings (knob mode, stick mode). Connection status is driven by the linked LED driver's `IsConnected()`, not global plugin state. Flag LED section and individual button swatches are shown/hidden based on `WheelModelInfo`. Paddle settings (mode, clutch split point) live on the plugin's Wheel tab, not here

*Dashboard:*
- `MozaDashDeviceExtension` — Dashboard device extension, same pattern as wheel
- `MozaDashLedDeviceManager` — Virtual `ILedDeviceManager` for the dashboard. Reports connected when the dash is detected. Combines RPM colors (from telemetry LEDs, bits 0-9) and flag colors (from button LEDs, bits 10-15) into a 16-bit bitmask sent via `dash-send-telemetry`. Unlike the wheel, no per-frame colors are sent — the dash firmware uses stored colors and only receives the on/off bitmask. Separate from the telemetry data streaming (handled by `TelemetrySender`), which sends full game data for dashboard display widgets
- `MozaDashExtensionSettings` — Dashboard-specific settings (brightness, indicator modes, colors)
- `MozaDashSettingsControl` — Status panel with indicator modes, brightness, and color swatches (RPM, blink, flags). Connection status driven by linked LED driver's `IsConnected()`

**Device Definitions** (`DeviceTemplates/`) — Device definitions using SimHub 9.11+'s Device Builder format. Each directory contains a `device.json` with the device's LED layout baked in, embedded as resources in the DLL. Deployed lazily to SimHub's `DevicesDefinitions/User/` directory when the matching device is first detected over serial — not at startup. A restart banner appears in the plugin settings panel when new definitions are deployed. Per-model wheel definitions are generated dynamically at runtime based on model-specific button counts.
- `MozaWheelGeneric/` — Generic fallback (14 buttons) for unknown new-protocol wheels
- `MozaWheelOldProto/` — Old-protocol (ES) wheels — RPM LEDs only
- `MozaDashShdp/` — Dashboard definition (10 RPM LEDs + 6 flag LEDs)

**Telemetry Data** (`Data/`) — Embedded resources for the telemetry streaming system:
- `Telemetry.json` — Channel definitions (150+ entries) describing the telemetry channels supported by Moza dashboards, each with URL, compression type, and package_level. Loaded by `DashboardProfileStore` at runtime

### Adding New Device Settings

When adding a new setting that is written to the device, it must also be saved/restored with the profile system:

1. **`MozaCommandDatabase.cs`** — Add command definition (name, device, read/write groups, command ID, payload size, type)
2. **`MozaDeviceManager.cs`** — Add device type mapping in `GetDeviceId()` if it's a new device
3. **`MozaProtocol.cs`** — Add device ID and read/write group constants if needed
4. **`MozaData.cs`** — Add volatile field(s) and `UpdateFromCommand` case(s)
5. **`MozaPlugin.cs`** — Add to `StatusPollCommands` (if it's a detection probe) and the appropriate per-device read array (e.g. `BaseSettingsReadCommands`, `NewWheelSettingsReadCommands`, `HandbrakeSettingsReadCommands`, etc.) so it's read after that device is detected. Add detection logic in `DetectDevices()` if needed, update `ApplyProfile()` (restore from profile → `_data` + device write), and update the device's `ApplySaved*Settings()` method so the value is written on detection
6. **`MozaProfile.cs`** — Add property (with -1 sentinel default), copy in `CopyProfilePropertiesFrom()`, capture in `CaptureFromCurrent()`
7. **`SettingsControl.xaml`** — Add UI controls
8. **`SettingsControl.xaml.cs`** — Add refresh logic and event handlers (handler must update `_data`, write to device, and call `SaveSettings()`). `SaveSettings()` is debounced (500ms) so rapid slider drags don't thrash the disk

Every setting that writes to the device on UI change must round-trip through profiles. If it's not in `MozaProfile`, it will be lost on game/profile switch.

### Adding a New Telemetry Channel

1. **`Data/Telemetry.json`** — Ensure the channel's URL, compression type, and package_level are defined
2. **`DashboardProfileStore.cs`** — Add the URL suffix → `SimHubField` mapping to `UrlFieldMap`
3. **`DashboardProfile.cs`** — Add the new value to the `SimHubField` enum
4. **`GameDataSnapshot.cs`** — Add a field, populate it in `FromStatusData()`, and add a case to `GetField()`

### Key Protocol Details

- Message format: `[0x7E] [length] [request_group] [device_id] [command_id...] [payload...] [checksum]`
- Response parsing: toggle bit 7 of request_group, swap nibbles of device_id, then match command_id (with 0xFF wildcards)
- Read messages use a zero-filled payload of the declared byte width (matches boxflat's `prepare_message`). Some wheels silently drop reads with a non-zero payload, even though the protocol nominally ignores payload bytes on reads
- All multi-byte integers are big-endian; floats are byte-reversed
- `MozaSerialConnection` uses `ConcurrentQueue` for writes with a 4ms inter-write gap (boxflat's proven timing; tuned to leave headroom for ~48Hz telemetry) and a polling read thread (2ms interval)

### Dependencies

- **NuGet:** `Microsoft.NETFramework.ReferenceAssemblies.net48`, `Newtonsoft.Json`, `log4net`
- **Runtime (Windows only):** `System.Management` — loaded via reflection for WMI port discovery; falls back to probe-based discovery if unavailable
- **SimHub DLLs** (checked into `libs/SimHub/`, reference-only, not packaged): `SimHub.Plugins.dll`, `GameReaderCommon.dll`, `SimHub.Logging.dll`, `SerialDash.dll`, `BA63Driver.dll`, `HidSharp.dll`. A daily GitHub Actions workflow automatically creates PRs when new SimHub versions are released.

**Important:** The SimHub DLLs in `libs/SimHub/` must match the runtime SimHub version. The PluginSdk ships older DLLs that may be missing interface members added in newer releases, causing `TypeLoadException` at runtime. Always update from the actual SimHub installation directory.
