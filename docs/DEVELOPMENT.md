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

### CI/CD

- **Build**: Every push to `main` and every PR is built automatically via GitHub Actions.
- **Release**: Pushing a `v*` tag (e.g., `v0.2.0`) builds a Release, generates a changelog, and publishes a GitHub Release with the zipped DLL.
- **SimHub dependency updates**: A daily workflow checks for new SimHub releases and creates a PR to update `libs/SimHub/`.

## Architecture

### Component Layers

**Plugin Entry Point** (`MozaPlugin.cs`) — Implements SimHub's `IPlugin`, `IDataPlugin`, `IWPFSettingsV2`. Manages lifecycle (Init/DataUpdate/End), connection state, auto-reconnect (5s timer), setting polling (2s timer), `TelemetrySender` for dashboard data streaming, and `DashboardProfileStore` for profile management. Detects base, wheel, dashboard, handbrake, and pedals. This is the orchestrator that wires everything together.

**Serial Protocol Layer** (`Protocol/`) — Binary protocol over USB serial at 115200 baud (VID `0x346E`):
- `MozaSerialConnection` — Device auto-discovery, background read/write threads, frame assembly
- `MozaCommand` — Message builder with big-endian int encoding
- `MozaCommandDatabase` — 150+ pre-built command definitions
- `MozaResponseParser` — Decodes responses using bit-7 toggle, nibble swap, and wildcard matching
- `MozaProtocol` — Constants (start byte `0x7E`, device IDs, checksum formula: `(13 + sum) % 256`)

**Device Management** (`MozaDeviceManager.cs`) — High-level read/write API. Handles wheel device ID cycling (IDs 23→21→19) since ES wheels respond on different IDs. `ResetWheelDetection()` must be called on disconnect so detection probes are re-sent on reconnect.

**Data Model** (`Telemetry/MozaData.cs`) — Thread-safe storage (~60 volatile fields) for all device values including base, wheel, dashboard, handbrake, and pedals. `UpdateFromCommand()` maps parsed responses to fields; `UpdateFromArray()` handles color/timing byte arrays.

**Dashboard Telemetry Streaming** (`Telemetry/`) — Streams game data to the wheel dashboard display via a multi-tier binary protocol:
- `TelemetrySender` — Timer-based sender running at configurable intervals per tier (30ms, 500ms, 2000ms). Also sends telemetry enable frames (~48 Hz), mode frames, sequence counters, heartbeats, and dash keepalives. Supports test patterns for diagnostics
- `TelemetryFrameBuilder` — Assembles complete Moza telemetry frames (`7E [N] 43 17 7D 23 ... [flag] 20 [data] [checksum]`) by bit-packing channel values from a `GameDataSnapshot`
- `TelemetryEncoder` — Encodes game values to compressed wire formats (25+ types: `float_001`, `percent_1`, `tyre_temp_1`, `int30`, etc.) with appropriate scaling and clamping
- `TelemetryBitWriter` — LSB-first bit packer that writes variable-width values into a byte buffer
- `GameDataSnapshot` — Flat struct populated from SimHub's `StatusDataBase` API, decoupling frame building from SimHub types. Normalizes units (e.g. throttle 0–100 → 0.0–1.0)
- `DashboardProfile` / `MultiStreamProfile` — Channel definitions grouped into tiers by update frequency. Each channel maps a Moza telemetry URL to a `SimHubField` enum, compression type, and bit width
- `DashboardProfileStore` — Parses `.mzdash` dashboard files (extracts `Telemetry.get()` URLs via regex), loads channel metadata from embedded `Data/Telemetry.json`, and maps URL suffixes to SimHub fields via a static `UrlFieldMap`
- `TelemetryDiagnostics` — Frame logging and test pattern generation

**UI** (`UI/`) — WPF settings with 4 tabs (Base, Wheel, Options, Telemetry). Uses `_suppressEvents` flag during 500ms refresh timer to prevent feedback loops. 30+ RGB color pickers via `ColorPickerDialog`. The Telemetry tab provides dashboard profile selection (builtin + load from `.mzdash`), diagnostic options (flag byte, mode frame, sequence counter toggles), test pattern controls, and frame log export.

**Profile System** (`UI/MozaProfile.cs`, `UI/MozaProfileStore.cs`) — Per-game configuration snapshots using SimHub's `ProfileBase`. Uses -1 sentinel to mark settings not included in a profile.

**Device Extension System** (`Devices/`) — Registers MOZA devices as SimHub devices so they appear in SimHub's Devices section with native LED effects support. Each device type (wheel, dashboard) has its own extension, LED manager, settings class, and settings control:

*Shared:*
- `MozaDeviceExtensionFilter` — `IDeviceExtensionFilter` that routes devices by `StandardDeviceId` to the correct extension (`MozaRacingWheel` → wheel, `MozaRacingDash` → dash)
- `MozaDeviceConstants` — `StandardDeviceId` strings and LED count constants (protocol maximums) for each device type
- `WheelModelInfo` — Per-model LED layout descriptor (button count, flag presence, button index remapping). Resolved from the firmware model name string after wheel detection. Known models: GS V2P (10 buttons, no flags), CS V2.1 (6 non-contiguous buttons), CSP/KSP/FSR2 (14 buttons, flags). Unknown models default to 14 buttons, no flags

*Wheel:*
- `MozaWheelDeviceExtension` — `DeviceExtension` subclass providing a settings tab and per-game device profiles via `GetSettings()`/`SetSettings()`
- `MozaLedDeviceManager` — Virtual `ILedDeviceManager` injected via reflection into the device's LED module. Reports connected when the wheel is detected (via `OnConnect`/`OnDisconnect` events fired from `UpdateConnectionState()`). Forwards computed `Color[]` to MOZA hardware as per-frame color chunks + bitmask in `Display()`. Button LED count and index mapping are model-aware via `WheelModelInfo` — non-contiguous layouts (e.g. CS V2.1) are remapped from SimHub's contiguous indices to the correct protocol positions
- `MozaWheelExtensionSettings` — Wheel-specific settings serialized to SimHub device profiles
- `MozaWheelSettingsControl` — Status panel with indicator modes, brightness, color swatches, and paddle settings. Flag LED section and individual button swatches are shown/hidden based on `WheelModelInfo`

*Dashboard:*
- `MozaDashDeviceExtension` — Dashboard device extension, same pattern as wheel
- `MozaDashLedDeviceManager` — Virtual `ILedDeviceManager` for the dashboard. Reports connected when the dash is detected. Converts SimHub LED colors to a 16-bit bitmask (bits 0-9 = RPM, bits 10-15 = flags) sent via `dash-send-telemetry`. Unlike the wheel, no per-frame colors are sent — the dash firmware uses stored colors and only receives the on/off bitmask. Separate from the telemetry data streaming (handled by `TelemetrySender`), which sends full game data for dashboard display widgets
- `MozaDashExtensionSettings` — Dashboard-specific settings (brightness, indicator modes, colors)
- `MozaDashSettingsControl` — Status panel with indicator modes, brightness, and color swatches (RPM, blink, flags)

**Device Templates** (`DeviceTemplates/`) — `.shdevicetemplate` ZIPs (built automatically) containing `device.json`, `defaults.json`, and `picture.png`. Deployed to SimHub's `StandardDevicesTemplatesUser/` directory. SimHub deletes these files when the user removes the device, so the plugin should re-deploy them.
- `MozaWheel/` → `MozaRacingWheel.shdevicetemplate` (10 RPM LEDs, optional 14 button LEDs)
- `MozaDash/` → `MozaRacingDash.shdevicetemplate` (16 LEDs: 10 RPM + 6 flag as single strip)

**Telemetry Data** (`Data/`) — Embedded resources for the telemetry streaming system:
- `Telemetry.json` — Channel definitions (150+ entries) describing the telemetry channels supported by Moza dashboards, each with URL, compression type, and package_level. Loaded by `DashboardProfileStore` at runtime

### Adding New Device Settings

When adding a new setting that is written to the device, it must also be saved/restored with the profile system:

1. **`MozaCommandDatabase.cs`** — Add command definition (name, device, read/write groups, command ID, payload size, type)
2. **`MozaDeviceManager.cs`** — Add device type mapping in `GetDeviceId()` if it's a new device
3. **`MozaProtocol.cs`** — Add device ID and read/write group constants if needed
4. **`MozaData.cs`** — Add volatile field(s) and `UpdateFromCommand` case(s)
5. **`MozaPlugin.cs`** — Add to `StatusPollCommands` (detection probe), `SettingsPollCommands` (read on connect), detection logic in `DetectDevices()`, and `ApplyProfile()` (restore from profile → `_data` + device write)
6. **`MozaProfile.cs`** — Add property (with -1 sentinel default), copy in `CopyProfilePropertiesFrom()`, capture in `CaptureFromCurrent()`
7. **`SettingsControl.xaml`** — Add UI controls
8. **`SettingsControl.xaml.cs`** — Add refresh logic and event handlers (handler must update `_data`, write to device, and call `SaveSettings()`)

Every setting that writes to the device on UI change must round-trip through profiles. If it's not in `MozaProfile`, it will be lost on game/profile switch.

### Adding a New Telemetry Channel

1. **`Data/Telemetry.json`** — Ensure the channel's URL, compression type, and package_level are defined
2. **`DashboardProfileStore.cs`** — Add the URL suffix → `SimHubField` mapping to `UrlFieldMap`
3. **`DashboardProfile.cs`** — Add the new value to the `SimHubField` enum
4. **`GameDataSnapshot.cs`** — Add a field, populate it in `FromStatusData()`, and add a case to `GetField()`

### Key Protocol Details

- Message format: `[0x7E] [length] [request_group] [device_id] [command_id...] [payload...] [checksum]`
- Response parsing: toggle bit 7 of request_group, swap nibbles of device_id, then match command_id (with 0xFF wildcards)
- All multi-byte integers are big-endian; floats are byte-reversed
- `MozaSerialConnection` uses `ConcurrentQueue` for writes and a polling read thread (2ms interval)

### Dependencies

- **NuGet:** `Microsoft.NETFramework.ReferenceAssemblies.net48`
- **Runtime (Windows only):** `System.Management` — loaded via reflection for WMI port discovery; falls back to probe-based discovery if unavailable
- **SimHub DLLs** (checked into `libs/SimHub/`, reference-only, not packaged): `SimHub.Plugins.dll`, `GameReaderCommon.dll`, `SimHub.Logging.dll`, `Newtonsoft.Json.dll`, `log4net.dll`, `SerialDash.dll`, `BA63Driver.dll`. A daily GitHub Actions workflow automatically creates PRs when new SimHub versions are released.

**Important:** The SimHub DLLs in `libs/SimHub/` must match the runtime SimHub version. The PluginSdk ships older DLLs that may be missing interface members added in newer releases, causing `TypeLoadException` at runtime. Always update from the actual SimHub installation directory.
