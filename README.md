# MOZA Plugin for SimHub

A SimHub plugin that communicates directly with MOZA Racing hardware over serial, providing full hardware configuration and LED control through SimHub's native device and effects system.

Built using the amazing work of [Boxflat](https://github.com/Lawstorant/boxflat) that reverse-engineered the [MOZA serial protocol](docs/moza-protocol.md).

![MOZA Plugin Settings](docs/Screenshot.png)

> [!NOTE]
> MOZA is a registered trademark of Gudsen Technology Co., Ltd. This project is not affiliated with, endorsed by, or sponsored by MOZA or Gudsen Technology. All trademarks are the property of their respective owners.

> [!IMPORTANT]
> **Close Pithouse before using this plugin.** Both applications communicate with MOZA hardware over the same serial port and cannot be open simultaneously. Pithouse must be fully closed (not just minimized) before SimHub can connect.

> [!WARNING]
> **USE AT YOUR OWN RISK.** This software communicates directly with force feedback hardware capable of producing high torque output that can cause serious injury or property damage. This plugin is provided "as is", without warranty of any kind, express or implied. The authors accept no responsibility or liability for any damage to hardware, injury to persons, or any other loss arising from the use of this software. By using this plugin, you acknowledge the inherent risks of controlling force feedback devices via third-party software and accept full responsibility for any consequences.

## Custom Effects managed by Simhub

https://github.com/user-attachments/assets/94ad3e6a-9ae0-46a2-8e2f-4f4343326414

_Thank you to a gracious tester who provided this custom Engine Start and Pit Limiter effects video._

**_Screen updates not currently supported._**

## Installation

Download the latest `MozaPlugin_v*.zip` from the [Releases](https://github.com/giantorth/moza-simhub-plugin/releases) page and extract into your SimHub installation directory:

- `MozaPlugin.dll` — copy to the SimHub root directory
- `MozaRacingWheel.shdevicetemplate` — copy to `DevicesDefaults/StandardDevicesTemplatesUser/` (create the folder if it doesn't exist)
- `MozaRacingDash.shdevicetemplate` — copy to `DevicesDefaults/StandardDevicesTemplatesUser/`

Restart SimHub — the plugin appears under Settings > Plugins as "MOZA Control". To use SimHub's LED effects system, go to Devices and add the "MOZA Racing Wheel" and/or "MOZA Racing Dash" devices.

## Features

### SimHub Device Integration

MOZA wheels and dashboards register as native SimHub devices, appearing in SimHub's **Devices** section. This enables full control of your LEDs through SimHub's effects pipeline — no separate telemetry mode needed.

![Device Panel](docs/Device.png)

- **LED Effects System** — Use SimHub's full Button and Telemetry effects configuration UI (RPM indicators, flags, speed limiter animations, scripted effects, etc.) to control your wheel and dashboard LEDs
- **Per-Game Device Profiles** — SimHub's device profile system saves and restores LED effect configurations per game
- **Auto-Detection** — MOZA devices are automatically detected by USB VID/PID when adding a new device
- **Separate Wheel & Dashboard Devices** — Each registers independently with its own profile and LED configuration

![LED Effects Configuration](docs/Leds.png)

The plugin injects virtual LED drivers so SimHub's effects UI shows each device as connected, even though MOZA uses a proprietary serial protocol. The computed LED colors are forwarded to the hardware each frame.

Tested:
- Old-protocol wheels (ES series)
- R5 Base
- Moza handbrake (directly attached)
- New-protocol wheels (Vision GS/CS V2P/TSW)

TBD:
- Other New-Protocol wheels (FSR/RS) (**testers needed**)

Untested:
- Dashboard (**testers needed**)


### Known Issues

- Gaps in LEDs for CS V2P: 6 buttons map across LEDs # 1,2,4,7,9,10
- Dashboards to LCDs in wheels not supported
- Only one wheel device in Simhub is possible currently, even with multiple wheels. Wheel identification is pending.

### Per-Game Profiles

All settings (base, wheel LEDs, dashboard, handbrake, pedals) are stored per-game using SimHub's built-in profile system. Profiles switch automatically when you launch a different game. A profile selector is shown at the top of the settings panel.

### Wheelbase Configuration (Base Tab)

Read/write control of wheelbase settings:

**Core Settings**

| Setting | Range | Notes |
|---------|-------|-------|
| Wheel Rotation Angle | 90-2700° | Sets both limit and max-angle |
| Game FFB Strength | 0-100% | |
| Base Torque Output | 50-100% | |
| Maximum Wheel Speed | 0-200% | |

**Wheelbase Effects**

| Setting | Range | Notes |
|---------|-------|-------|
| Wheel Damper | 0-100% | |
| Wheel Friction | 0-100% | |
| Natural Inertia | 100-500 | |
| Wheel Spring | 0-100% | |
| FFB Reversal | On/Off | |

**Game Effects**

| Setting | Range | Notes |
|---------|-------|-------|
| Game Damper | 0-100% | Default 50% |
| Game Friction | 0-100% | Default 50% |
| Game Inertia | 0-100% | Default 50% |
| Game Spring | 0-100% | Default 100% |

**High Speed Damping**

| Setting | Range | Notes |
|---------|-------|-------|
| High Speed Damping Level | 0-100% | |
| High Speed Damping Trigger | 0-400 kph | |

**Protection**

| Setting | Range | Notes |
|---------|-------|-------|
| Hands-Off Protection | On/Off | |
| Steering Wheel Inertia | 100-4000 | For protection mode |

**Soft Limit**

| Setting | Range | Notes |
|---------|-------|-------|
| Soft Limit Stiffness | 1-10 | |
| Soft Limit Retain Game FFB | On/Off | |

**FFB Equalizer**

6-band equalizer for fine-tuning force feedback frequency response:

| Band | Range |
|------|-------|
| 10 Hz | 0-400% |
| 15 Hz | 0-400% |
| 25 Hz | 0-400% |
| 40 Hz | 0-400% |
| 60 Hz | 0-400% |
| 100 Hz | 0-400% |

100% is flat/default. Values below 100% attenuate, above 100% boost.

**FFB Output Curve**

5-point force feedback output curve with presets (Linear, S Curve, Exponential, Parabolic):

| Point | Input | Output Range |
|-------|-------|-------------|
| Y1 | 20% | 0-100% |
| Y2 | 40% | 0-100% |
| Y3 | 60% | 0-100% |
| Y4 | 80% | 0-100% |
| Y5 | 100% | 0-100% |

**Miscellaneous**

| Setting | Range |
|---------|-------|
| Standby Mode | On/Off |
| Base Status LED | On/Off |
| Bluetooth | On/Off |

**Live Temperature Monitoring**

- MCU Temperature
- MOSFET Temperature
- Motor Temperature

### Wheel Configuration (Wheel Tab)

The Wheel tab auto-detects which wheel is connected and shows the appropriate settings. A **Test LEDs** button is available for both wheel types.

#### New-Protocol Wheels (GS/FSR/CS/RS/TSW)

| Setting | Range | Notes |
|---------|-------|-------|
| Telemetry Mode | Off / Telemetry / Static | |
| Idle Effect | Off / Constant / Breathing / Color Cycle / Rainbow / Sand Flow | |
| RPM LED Colors | 10 RGB color pickers | Click swatch to open picker |
| RPM Brightness | 0-100 | |
| Flag LED Colors | 6 RGB color pickers | |
| Flags Brightness | 0-100 | |
| Button LED Colors | 14 RGB color pickers | Includes TSW buttons |
| Buttons Brightness | 0-100 | |
| Button Idle Effect | Off / Constant / Breathing / Color Cycle / Rainbow / Sand Flow | |
| Paddles Mode | Buttons / Combined / Split | |
| Clutch Split Point | 0-100% | Only shown in Split mode |
| Knob Mode | Mode 0-3 | |
| Stick as D-Pad | On/Off | |

#### ES Wheels (Old Protocol)

| Setting | Range | Notes |
|---------|-------|-------|
| RPM Indicator Mode | RPM / Off / On | |
| RPM Display Mode | Mode 1 / Mode 2 | |
| RPM Brightness | 0-15 | |

### Dashboard LED Configuration (Dashboard Tab)

> Auto-detected — tab is hidden if no dashboard is connected.

| Setting | Range | Notes |
|---------|-------|-------|
| RPM Indicator Mode | Off / RPM / On | |
| RPM Display Mode | Mode 1 / Mode 2 | |
| RPM LED Colors | 10 RGB color pickers | |
| RPM Brightness | 0-15 | |
| Flags Mode | Off / Flags / On | |
| Flag LED Colors | 6 RGB color pickers | |
| Flags Brightness | 0-15 | |

### Handbrake Configuration (Handbrake Tab)

> Auto-detected — tab is hidden if no handbrake is connected.

| Setting | Range | Notes |
|---------|-------|-------|
| Mode | Axis / Button | |
| Button Threshold | 0-100% | Trigger point for button mode |
| Reverse Direction | On/Off | |
| Range Start | 0-100% | |
| Range End | 0-100% | |

**Output Curve** — 5-point curve with presets (Linear, S Curve, Exponential, Parabolic), same format as the FFB output curve. Calibration start/stop buttons are also available.

### Pedals Configuration (Pedals Tab)

> Auto-detected — tab is hidden if no pedals are connected.

Settings for **Throttle**, **Brake**, and **Clutch** (each configured independently):

| Setting | Range | Notes |
|---------|-------|-------|
| Reverse Direction | On/Off | |
| Range Start | 0-100% | |
| Range End | 0-100% | |
| Output Curve | 5-point with presets | Linear, S Curve, Exponential, Parabolic |
| Calibration | Start/Stop | Interactive calibration |

Brake has an additional **Sensor Ratio** slider (0-100%) to blend between angle sensor (0%) and load cell (100%).

### Options Tab

| Setting | Description |
|---------|-------------|
| Apply profile settings on launch | Automatically apply the saved profile when a game starts |
| Limit wheel updates | Only send LED updates when data changes (fixes flickering on some wheels) |
| Wheel keepalive | Resend LED state every ~1 second (keeps ES wheels in telemetry mode) |
| Always resend bitmask | Resend bitmask with every color update (fixes wheels that miss color changes) |
| Connection enabled | Toggle serial connection to MOZA hardware |
| Clear All Settings & Profiles | Reset all plugin settings and profiles to defaults |

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

## Building from Source

See [DEVELOPMENT.md](docs/DEVELOPMENT.md) for a full project overview and build reference.

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

The plugin communicates with MOZA hardware over USB serial (CDC/ACM) at 115200 baud using the [MOZA binary protocol](docs/moza-protocol.md):

```
[0x7E] [payload_length] [request_group] [device_id] [command_id...] [payload...] [checksum]
```

- **Device discovery**: Scans Windows COM ports for USB VID `0x346E` (Gudsen/Moza)
- **Auto-reconnect**: Retries every 5 seconds if disconnected
- **Background threads**: Separate read and write threads prevent blocking
- **Checksum**: `(13 + sum_of_all_bytes) % 256`

### LED Pipeline

See [SimHub Plugin API Reference](docs/simhub.md) for notes on the plugin interfaces used.

The plugin registers MOZA wheel and dashboard as native SimHub LED devices by injecting virtual LED drivers (`MozaLedDeviceManager` / `MozaDashLedDeviceManager`). SimHub's effects pipeline computes LED colors each frame (RPM indicators, flags, animations, scripted effects, etc.) and calls `Display()` on the virtual driver, which converts the colors to MOZA's bitmask/color protocol and sends them to hardware over serial.

- **Wheel**: RPM bitmask (10 LEDs) + button bitmask (14 LEDs) sent as separate commands
- **Dashboard**: Combined bitmask (10 RPM + 6 flag LEDs) sent as a single 16-bit value
- **Brightness**: Forwarded from SimHub's per-device brightness setting
- **Keepalive**: Periodic resend (~1s) to prevent LED timeout on some hardware

### Debugging / Contributing

To capture USB traffic between your MOZA device and PC for diagnosing issues or reverse-engineering new commands, see [USB Traffic Capture](docs/usb-capture.md).

### Settings Read/Write Flow

- **Read**: Plugin sends a read request; device responds with the current value
- **Write**: Plugin sends the new value; device applies it immediately
- **Polling**: Temperatures polled every 2 seconds; all other settings read on connect and manual refresh
- **UI update**: Settings control refreshes from the data model every 500ms

## Project Structure

```
MozaPlugin.cs                      Main plugin class (IPlugin, IDataPlugin, IWPFSettingsV2)
MozaDeviceManager.cs               Read/write API for device settings
Protocol/
  MozaProtocol.cs                  Protocol constants (start byte, device IDs, checksums)
  MozaCommand.cs                   Message builder (read/write/int/array)
  MozaCommandDatabase.cs           150+ command definitions from serial.md
  MozaResponseParser.cs            Response decoder (bit 7 toggle, nibble swap, wildcard matching)
  MozaSerialConnection.cs          Serial port I/O with auto-discovery and background threads
Telemetry/
  MozaData.cs                      Thread-safe data model for all device values
Devices/
  MozaDeviceExtensionFilter.cs     Attaches extensions to MOZA devices in SimHub's device system
  MozaDeviceConstants.cs           Shared device type IDs and LED counts
  MozaWheelDeviceExtension.cs      Wheel device extension — profiles, LED driver injection
  MozaWheelExtensionSettings.cs    Wheel settings for SimHub device profiles
  MozaWheelSettingsControl.xaml(.cs) Status panel for the wheel device extension tab
  MozaLedDeviceManager.cs          Virtual wheel LED driver — spoofs connection, forwards LED colors
  MozaDashDeviceExtension.cs       Dashboard device extension — profiles, LED driver injection
  MozaDashExtensionSettings.cs     Dashboard settings for SimHub device profiles
  MozaDashSettingsControl.xaml(.cs) Status panel for the dashboard device extension tab
  MozaDashLedDeviceManager.cs      Virtual dashboard LED driver — spoofs connection, forwards bitmask
DeviceTemplates/
  MozaWheel/                       Source files for wheel .shdevicetemplate (zipped at build time)
  MozaDash/                        Source files for dashboard .shdevicetemplate (zipped at build time)
UI/
  SettingsControl.xaml(.cs)        WPF settings UI (Base, Wheel, Dashboard*, Handbrake*, Pedals*, Options tabs; *autodetected)
  ColorPickerDialog.xaml(.cs)      RGB color picker dialog
  MozaPluginSettings.cs            Persisted plugin settings (brightness, timings, colors)
  MozaProfile.cs                   Per-game configuration snapshot (80+ settings)
  MozaProfileStore.cs              SimHub profile storage integration
```
