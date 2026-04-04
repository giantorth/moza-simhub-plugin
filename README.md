# MOZA Plugin for SimHub

A SimHub plugin that communicates directly with MOZA Racing hardware over serial, providing telemetry: it sends in-game RPM data to your wheel/dashboard LEDs **and** lets you configure your wheelbase, wheel LEDs, and dashboard from within SimHub.

Built using the amazing work of the [boxflat](https://github.com/Lawstorant/boxflat) project that reverse-engineered the [MOZA serial protocol](../moza-protocol.md).

![MOZA Plugin Settings](docs/Screenshot.png)

> [!WARNING]
> **USE AT YOUR OWN RISK.** This software communicates directly with force feedback hardware capable of producing high torque output that can cause serious injury or property damage. This plugin is provided "as is", without warranty of any kind, express or implied. The authors accept no responsibility or liability for any damage to hardware, injury to persons, or any other loss arising from the use of this software. By using this plugin, you acknowledge the inherent risks of controlling force feedback devices via third-party software and accept full responsibility for any consequences.

## Features

### Game Telemetry Output (RPM LEDs)

Sends real-time RPM data from any SimHub-supported game to your MOZA wheel and dashboard LEDs:

- **10-LED RPM bar** lights up progressively as RPM climbs
- **Blink at redline** (95%+ RPM range, or when SimHub's car redline is reached in SimHub mode)
- **Three RPM modes**: Percent (% of max RPM), RPM (absolute thresholds), or SimHub (uses SimHub's Car Settings shift light zones)
- **Automatic idle-to-max scaling** using the game's reported RPM range
- **Keepalive** sends at least once per second to prevent LED timeout
- **LED clear** on game exit and plugin shutdown

Tested:
- Old-protocol wheels (ES series)
- R5 Base
- Moza handbrake (directly attached)

Untested:
- Dashboard (**testers needed**)
- New-protocol wheels (GS/FSR/CS/RS/TSW) (**testers needed**)

### Wheelbase Configuration (Base Tab)

Read/write control of wheelbase settings:

| Setting | Range | Notes |
|---------|-------|-------|
| Wheel Rotation Angle | 90-2700° | Sets both limit and max-angle |
| Game FFB Strength | 0-100% | |
| Base Torque Output | 50-100% | |
| Maximum Wheel Speed | 0-200% | |
| Wheel Damper | 0-100% | |
| Wheel Friction | 0-100% | |
| Natural Inertia | 100-500 | |
| Wheel Spring | 0-100% | |
| FFB Reversal | On/Off | |
| Game Damper | 0-100% | Default 50% |
| Game Friction | 0-100% | Default 50% |
| Game Inertia | 0-100% | Default 50% |
| Game Spring | 0-100% | Default 100% |
| High Speed Damping Level | 0-100% | |
| High Speed Damping Trigger | 0-400 kph | |
| Hands-Off Protection | On/Off | |
| Steering Wheel Inertia | 100-4000 | For protection mode |
| Soft Limit Stiffness | 1-10 | |
| Soft Limit Retain Game FFB | On/Off | |
| Standby Mode | On/Off | |
| Base Status LED | On/Off | |

Live temperature monitoring:
- MCU Temperature
- MOSFET Temperature
- Motor Temperature

### Wheel LED Configuration (Wheel LEDs Tab)

| Setting | Range | Notes |
|---------|-------|-------|
| Telemetry Mode | Off / Telemetry / Static | |
| Idle Effect | Off / Constant / Breathing / Color Cycle / Rainbow / Sand Flow | |
| RPM LED Colors | 10 RGB color pickers | Click swatch to open picker |
| RPM Brightness | 0-100 | |
| RPM Blink Interval | 0-1000 ms | |
| Flag LED Colors | 6 RGB color pickers | |
| Flags Brightness | 0-100 | |
| Button LED Colors | 14 RGB color pickers | Includes TSW buttons |
| Buttons Brightness | 0-100 | |
| Button Idle Effect | Off / Constant / Breathing / Color Cycle / Rainbow / Sand Flow | |

### Dashboard LED Configuration (Dashboard Tab)

| Setting | Range | Notes |
|---------|-------|-------|
| RPM Indicator Mode | Off / RPM / On | |
| RPM Display Mode | Mode 1 / Mode 2 | |
| RPM Mode | Percent / RPM / SimHub | SimHub mode uses Car Settings shift light zones |
| RPM LED Colors | 10 RGB color pickers | |
| RPM Brightness | 0-15 | |
| Blink Interval | 0-1000 ms | |
| Flags Mode | Off / Flags / On | |
| Flag LED Colors | 6 RGB color pickers | |
| Flags Brightness | 0-15 | |

### SimHub Properties

The plugin exposes these properties for use in SimHub dashboards and overlays:

| Property | Type | Description |
|----------|------|-------------|
| `Moza.BaseConnected` | bool | Wheelbase connection status |
| `Moza.McuTemp` | double | MCU temperature (°C) |
| `Moza.MosfetTemp` | double | MOSFET temperature (°C) |
| `Moza.MotorTemp` | double | Motor temperature (°C) |
| `Moza.BaseState` | int | Wheelbase state |
| `Moza.FfbStrength` | int | FFB strength (%) |
| `Moza.MaxAngle` | int | Max steering angle (degrees) |

## Installation

Download the latest `MozaPlugin_v*.zip` from the [Releases](https://github.com/giantorth/moza-simhub-plugin/releases) page, extract `MozaPlugin.dll`, and copy it into your SimHub installation directory. Restart SimHub — the plugin appears under Settings > Plugins as "MOZA Control".

## Building from Source

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

## How It Works

### Serial Protocol

The plugin communicates with MOZA hardware over USB serial (CDC/ACM) at 115200 baud using the MOZA binary protocol:

```
[0x7E] [payload_length] [request_group] [device_id] [command_id...] [payload...] [checksum]
```

- **Device discovery**: Scans Windows COM ports for USB VID `0x346E` (Gudsen/Moza)
- **Auto-reconnect**: Retries every 5 seconds if disconnected
- **Background threads**: Separate read and write threads prevent blocking
- **Checksum**: `(13 + sum_of_all_bytes) % 256`

### RPM Telemetry Flow

On every SimHub frame (~60fps):

1. Read current RPM, max RPM, and idle RPM from `GameData`
2. Calculate RPM as percentage of usable range: `(rpm - idle) / (max - idle) * 100`
3. Map to 10-bit LED bitmask using the configured RPM mode:
   - **Percent**: user-configured % thresholds per LED
   - **RPM**: user-configured absolute RPM thresholds per LED
   - **SimHub**: uses SimHub's Car Settings shift light zones (3 LEDs per zone: shift 1 / shift 2 / redline)
4. At 95%+ (or when SimHub's redline is reached in SimHub mode), toggle all LEDs on/off every 4 frames for blink effect
5. Send bitmask to dashboard (`write group 65, cmd [253, 222]`) and/or wheel (`write group 63, cmd [26, 0]`)

### Settings Read/Write Flow

- **Read**: Plugin sends a read request; device responds with the current value
- **Write**: Plugin sends the new value; device applies it immediately
- **Polling**: Temperatures polled every 2 seconds; all other settings read on connect and manual refresh
- **UI update**: Settings control refreshes from the data model every 500ms

## Project Structure

```
MozaPlugin.cs             Main plugin class (IPlugin, IDataPlugin, IWPFSettingsV2)
MozaDeviceManager.cs               Read/write API for device settings
Protocol/
  MozaProtocol.cs                  Protocol constants (start byte, device IDs, checksums)
  MozaCommand.cs                   Message builder (read/write/int/array)
  MozaCommandDatabase.cs           150+ command definitions from serial.yml
  MozaResponseParser.cs            Response decoder (bit 7 toggle, nibble swap, wildcard matching)
  MozaSerialConnection.cs          Serial port I/O with auto-discovery and background threads
Telemetry/
  MozaData.cs             Thread-safe data model for all device values
  TelemetrySender.cs               RPM LED telemetry output logic
UI/
  SettingsControl.xaml(.cs)        WPF settings UI (Base, Wheel LEDs, Dashboard*, Handbrake* tabs; *autodetected)
  ColorPickerDialog.xaml(.cs)      RGB color picker dialog
  MozaPluginSettings.cs            Persisted plugin settings (brightness, timings, colors)
  MozaProfile.cs                   Per-game configuration snapshot
  MozaProfileStore.cs              SimHub profile storage integration
```

## Supported Devices

- Wheelbases: R5, R9, R12, R16, R21
- Wheels: ES series (tested), GS/FSR/CS/RS/TSW (untested)
- Dashboards: All MOZA digital dashes (untested)
