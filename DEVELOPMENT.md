# Project Overview

SimHub plugin for MOZA Racing hardware providing two-way telemetry: sends in-game RPM data to wheel/dashboard LEDs and allows configuring wheelbase settings. Uses a custom binary serial protocol reverse-engineered from the [boxflat](https://github.com/Lawstorant/boxflat) project.

## Build Commands

```bash
# Build (SimHub DLLs are in libs/SimHub/, no env var needed)
dotnet build -c Release

# Build and auto-deploy to a local SimHub installation
SIMHUB_PATH="C:\Program Files (x86)\SimHub" dotnet build -c Release

# Deploy skill available via /deploy
```

The project targets .NET Framework 4.8 (x86) and uses the `Microsoft.NETFramework.ReferenceAssemblies.net48` NuGet package so it can cross-compile on Linux without Mono. The built DLL runs on Windows under SimHub.

## Architecture

### Component Layers

**Plugin Entry Point** (`MozaPlugin.cs`) — Implements SimHub's `IPlugin`, `IDataPlugin`, `IWPFSettingsV2`. Manages lifecycle (Init/DataUpdate/End), connection state, auto-reconnect (5s timer), and setting polling (2s timer). This is the orchestrator that wires everything together.

**Serial Protocol Layer** (`Protocol/`) — Binary protocol over USB serial at 115200 baud (VID `0x346E`):
- `MozaSerialConnection` — Device auto-discovery, background read/write threads, frame assembly
- `MozaCommand` — Message builder with big-endian int encoding
- `MozaCommandDatabase` — 150+ pre-built command definitions
- `MozaResponseParser` — Decodes responses using bit-7 toggle, nibble swap, and wildcard matching
- `MozaProtocol` — Constants (start byte `0x7E`, device IDs, checksum formula: `(13 + sum) % 256`)

**Device Management** (`MozaDeviceManager.cs`) — High-level read/write API. Handles wheel device ID cycling (IDs 23→21→19) since ES wheels respond on different IDs. `ResetWheelDetection()` must be called on disconnect so detection probes are re-sent on reconnect.

**Data Model** (`Telemetry/MozaData.cs`) — Thread-safe storage (~60 volatile fields) for all device values. `UpdateFromCommand()` maps parsed responses to fields; `UpdateFromArray()` handles color/timing byte arrays.

**UI** (`UI/`) — WPF settings with 4 tabs (Base, Wheel LEDs, Dashboard, Handbrake). The Dashboard and Handbrake tabs are hidden until the respective device is detected. Uses `_suppressEvents` flag during 500ms refresh timer to prevent feedback loops. 30+ RGB color pickers via `ColorPickerDialog`.

**Profile System** (`UI/MozaProfile.cs`, `UI/MozaProfileStore.cs`) — Per-game configuration snapshots using SimHub's `ProfileBase`. Uses -1 sentinel to mark settings not included in a profile.

**Device Extension System** (`Devices/`) — Registers MOZA devices as SimHub devices so they appear in SimHub's Devices section with native LED effects support. Each device type (wheel, dashboard) has its own extension, LED manager, settings class, and settings control:

*Shared:*
- `MozaDeviceExtensionFilter` — `IDeviceExtensionFilter` that routes devices by `StandardDeviceId` to the correct extension (`MozaRacingWheel` → wheel, `MozaRacingDash` → dash)
- `MozaDeviceConstants` — `StandardDeviceId` strings and LED count constants for each device type

*Wheel:*
- `MozaWheelDeviceExtension` — `DeviceExtension` subclass providing a settings tab and per-game device profiles via `GetSettings()`/`SetSettings()`
- `MozaLedDeviceManager` — Virtual `ILedDeviceManager` injected via reflection into the device's LED module. Reports connected when the wheel is detected (via `OnConnect`/`OnDisconnect` events fired from `UpdateConnectionState()`). Forwards computed `Color[]` to MOZA hardware as per-frame color chunks + bitmask in `Display()`
- `MozaWheelExtensionSettings` — Wheel-specific settings serialized to SimHub device profiles
- `MozaWheelSettingsControl` — Status panel with indicator modes, brightness, color swatches, and paddle settings

*Dashboard:*
- `MozaDashDeviceExtension` — Dashboard device extension, same pattern as wheel
- `MozaDashLedDeviceManager` — Virtual `ILedDeviceManager` for the dashboard. Reports connected when the dash is detected. Converts SimHub LED colors to a 16-bit bitmask (bits 0-9 = RPM, bits 10-15 = flags) sent via `dash-send-telemetry`. Unlike the wheel, no per-frame colors are sent — the dash firmware uses stored colors and only receives the on/off bitmask
- `MozaDashExtensionSettings` — Dashboard-specific settings (brightness, indicator modes, colors)
- `MozaDashSettingsControl` — Status panel with indicator modes, brightness, and color swatches (RPM, blink, flags)

**Device Templates** (`DeviceTemplates/`) — `.shdevicetemplate` ZIPs (built automatically) containing `device.json`, `defaults.json`, and `picture.png`. Deployed to SimHub's `StandardDevicesTemplatesUser/` directory. SimHub deletes these files when the user removes the device, so the plugin should re-deploy them.
- `MozaWheel/` → `MozaRacingWheel.shdevicetemplate` (10 RPM LEDs, optional 14 button LEDs)
- `MozaDash/` → `MozaRacingDash.shdevicetemplate` (16 LEDs: 10 RPM + 6 flag as single strip)

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

### Key Protocol Details

- Message format: `[0x7E] [length] [request_group] [device_id] [command_id...] [payload...] [checksum]`
- Response parsing: toggle bit 7 of request_group, swap nibbles of device_id, then match command_id (with 0xFF wildcards)
- All multi-byte integers are big-endian; floats are byte-reversed
- `MozaSerialConnection` uses `ConcurrentQueue` for writes and a polling read thread (2ms interval)

### Dependencies

- **NuGet:** `System.IO.Ports`, `Microsoft.NETFramework.ReferenceAssemblies.net48`
- **Runtime (Windows only):** `System.Management` — loaded via reflection for WMI port discovery; falls back to probe-based discovery if unavailable
- **SimHub DLLs** (checked into `libs/SimHub/`, reference-only, not packaged): `SimHub.Plugins.dll`, `GameReaderCommon.dll`, `SimHub.Logging.dll`, `Newtonsoft.Json.dll`, `log4net.dll`, `SerialDash.dll`, `BA63Driver.dll`. A daily GitHub Actions workflow automatically creates PRs when new SimHub versions are released.

**Important:** The SimHub DLLs in `libs/SimHub/` must match the runtime SimHub version. The PluginSdk ships older DLLs that may be missing interface members added in newer releases, causing `TypeLoadException` at runtime. Always update from the actual SimHub installation directory.
