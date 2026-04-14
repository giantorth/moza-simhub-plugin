# MOZA Plugin for SimHub

A SimHub plugin that communicates directly with MOZA Racing hardware over serial, providing full hardware configuration and LED control through SimHub's native device and effects system.

Built using the amazing work of [Boxflat](https://github.com/Lawstorant/boxflat) that reverse-engineered the [MOZA serial protocol](docs/moza-protocol.md).

## Why This Exists

MOZA makes excellent sim racing hardware, but their companion software — Pithouse — is Windows-only. Linux users have no official way to manage LED effects or stream telemetry to your wheel's dashboard. SimHub, on the other hand, runs on Linux (via Proton/Wine), opening the door for cross-platform hardware control with built-in telemetry support.

The goal is to expand the functionality of MOZA devices to a wider audience by providing tools that work across multiple platforms.  

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

Restart SimHub — the plugin appears under Settings > Plugins as "MOZA Control".

**Device setup:** Connect your hardware and restart SimHub. The plugin auto-detects connected devices (wheel model, dashboard) and deploys matching device definitions. A banner in the plugin settings panel will prompt you to restart SimHub, after which the devices appear under Devices ready to add. Requires SimHub 9.11+.

## Features

### SimHub Device Integration

MOZA wheels and dashboards register as native SimHub devices, appearing in SimHub's **Devices** section. This enables full control of your LEDs through SimHub's effects pipeline — no separate telemetry mode needed.

![Device Panel](docs/Device.png)

- **Per-Model Device Definitions** — Each wheel model (CS Pro, KS Pro, FSR V2, GS V2 Pro, CS V2, etc.) has its own device definition with the correct LED layout baked in. Definitions are deployed automatically on first detection — just connect your hardware, restart SimHub, and add the device. Requires SimHub 9.11+
- **LED Effects System** — Use SimHub's full Button and Telemetry effects configuration UI (RPM indicators, flags, speed limiter animations, scripted effects, etc.) to control your wheel and dashboard LEDs
- **Per-Game Device Profiles** — SimHub's device profile system saves and restores LED effect configurations per game
- **Model-Aware Connection** — Only the device matching the currently connected wheel reports as connected. Swap wheels and the correct device activates automatically
- **Separate Wheel & Dashboard Devices** — Each registers independently with its own profile and LED configuration

![LED Effects Configuration](docs/Leds.png)

The plugin injects virtual LED drivers so SimHub's effects UI shows each device as connected, even though MOZA uses a proprietary serial protocol. The computed LED colors are forwarded to the hardware each frame.

![Effects List](docs/Effects.png)

SimHub contains many effects to choose from and this plugin supports any custom effects that target a device.

Tested:
- Old-protocol wheels (ES series)
- R5 Base
- Moza handbrake (directly attached)
- New-protocol wheels (Vision GS/CS V2P/TSW)

TBD:
- Other New-Protocol wheels (FSR/RS) (**testers needed**)

Untested:
- Dashboard (**testers needed**)


### Per-Model LED Configuration

Each wheel model has a dedicated SimHub device definition with the correct LED layout. The plugin detects the connected wheel model via firmware queries and deploys the matching definition on first detection.

| Device Name | Model Prefix | RPM | Buttons | Flags | Button Mapping |
|-------------|:------------:|:---:|:-------:|:-----:|----------------|
| MOZA GS V2 Pro | GS V2P | 10 | 10 | No | Contiguous (5 left + 5 right) |
| MOZA CS V2 | CS V2.1 | 10 | 6 | No | Non-contiguous: positions 1,2,4,7,9,10 |
| MOZA CS Pro | CSP | 10 | 14 | Yes | Contiguous |
| MOZA KS Pro | KSP | 10 | 14 | Yes | Contiguous |
| MOZA KS | KS | 10 | 10 | No | Contiguous |
| MOZA FSR V2 | FSR2 | 10 | 14 | Yes | Contiguous |
| MOZA Racing Wheel | *(generic)* | 10 | 14 | No | Contiguous (fallback for unknown models) |
| MOZA Old Protocol Wheel | *(ES wheels)* | 10 | 0 | No | RPM LEDs only |
| MOZA Dashboard | — | 10 | 0 | Yes | RPM + flag LEDs |

If your wheel model isn't listed, the generic "MOZA Racing Wheel" definition is deployed. Check the SimHub log for the `[Moza] Wheel model:` line and report the model name string so a dedicated definition can be added.

### Known Issues

- Dashboard LCD/screen updates are a work in progress and may or may not work
- Flag LEDs are managed through the plugin's own settings (Wheel tab), not through SimHub's effects UI

### Per-Game Profiles

All settings (base, wheel LEDs, dashboard telemetry, dashboard LEDs, handbrake, pedals) are stored per-game using SimHub's built-in profile system. Profiles switch automatically when you launch a different game. A profile selector is shown at the top of the settings panel.

### Plugin Panel (Settings > Plugins > MOZA Control)

#### Wheelbase Configuration (Base Tab)

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

#### Dashboard LED Configuration (Dashboard Tab)

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

#### Handbrake Configuration (Handbrake Tab)

> Auto-detected — tab is hidden if no handbrake is connected.

| Setting | Range | Notes |
|---------|-------|-------|
| Mode | Axis / Button | |
| Button Threshold | 0-100% | Trigger point for button mode |
| Reverse Direction | On/Off | |
| Range Start | 0-100% | |
| Range End | 0-100% | |

**Output Curve** — 5-point curve with presets (Linear, S Curve, Exponential, Parabolic), same format as the FFB output curve. Calibration start/stop buttons are also available.

#### Pedals Configuration (Pedals Tab)

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

#### Options Tab

| Setting | Description |
|---------|-------------|
| Apply profile settings on launch | Automatically apply the saved profile when a game starts |
| Limit wheel updates | Only send LED updates when data changes (fixes flickering on some wheels) |
| Wheel keepalive | Resend LED state every ~1 second (keeps ES wheels in telemetry mode) |
| Always resend bitmask | Resend bitmask with every color update (fixes wheels that miss color changes) |
| Connection enabled | Toggle serial connection to MOZA hardware |
| Clear All Settings & Profiles | Reset all plugin settings and profiles to defaults |

#### Telemetry Diagnostics (Telemetry Tab)

Diagnostic and protocol-level telemetry controls. The main telemetry settings (enable, profile selection) live on the Wheel device page and are saved per-wheel-profile.

| Setting | Description |
|---------|-------------|
| Flag byte | Diagnostic: override the tier flag byte sent in telemetry frames |
| Send mode frame | Periodically send mode frame (0x40/28:02) to keep wheel in multi-channel mode |
| Send sequence counter | Send sequence counter (0x2D) to base |
| Test pattern | Send cycling test data (gear 1-6, brake 0-100%, speed 0-200 km/h) for verifying dashboard display |
| Export frame log | Save recent telemetry frames to a file for debugging |

### Device Pages (Devices > MOZA Wheel / MOZA Dashboard)

These settings live on each device's page in SimHub's Devices section. They are saved per-device-per-game, so different games can have different configurations per wheel.

#### Wheel (MOZA Wheel tab)

The wheel device page auto-detects which wheel is connected and shows the appropriate settings.

**Dashboard Telemetry**

Streams game data (speed, RPM, gear, lap times, fuel, tyre wear, etc.) to the wheel's dashboard display using Moza's multi-tier binary telemetry protocol.

| Setting | Description |
|---------|-------------|
| Enable dashboard telemetry | Toggle telemetry streaming to the dashboard |
| Dashboard profile | Select a builtin profile or load a `.mzdash` file |
| Test pattern | Send cycling test data (gear 1-6, brake 0-100%, speed 0-200 km/h) for verifying dashboard display |

> [!WARNING]
> **Dashboard telemetry streaming is a work in progress.** It may not work correctly with all wheel/dashboard combinations. If you'd like to help test and improve this feature, please open an issue with your hardware details and any observations.

**New-Protocol Wheels (GS/FSR/CS/RS/TSW)**

| Setting | Range | Notes |
|---------|-------|-------|
| Telemetry Mode | Off / Telemetry / Static | |
| Idle Effect | Off / Constant / Breathing / Color Cycle / Rainbow / Sand Flow | |
| Flag LED Colors | 6 RGB color pickers | Only shown for models with flag LEDs (CSP, KSP, FSR2) |
| Flags Brightness | 0-100 | Only shown for models with flag LEDs |
| Button LED Colors | Per-model RGB color pickers | Count and mapping adapt to detected wheel model |
| Button Idle Effect | Off / Constant / Breathing / Color Cycle / Rainbow / Sand Flow | |
| Paddles Mode | Buttons / Combined / Split | |
| Clutch Split Point | 0-100% | Only shown in Split mode |
| Knob Mode | Mode 0-3 | |
| Stick as D-Pad | On/Off | |

**ES Wheels (Old Protocol)**

| Setting | Range | Notes |
|---------|-------|-------|
| RPM Indicator Mode | RPM / Off / On | |
| RPM Display Mode | Mode 1 / Mode 2 | |

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

See [DEVELOPMENT.md](docs/DEVELOPMENT.md) for build instructions (Windows & Linux cross-compilation), CI/CD pipeline details, and full architecture reference.

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

- **Wheel**: RPM bitmask (10 LEDs) + button bitmask (per-model count, remapped for non-contiguous layouts) sent as separate commands
- **Dashboard**: RPM bitmask (bits 0-9) + flag bitmask (bits 10-15) combined into a single 16-bit value from separate telemetry and button LED channels
- **Brightness**: Forwarded from SimHub's per-device brightness setting
- **Keepalive**: Periodic resend (~1s) to prevent LED timeout on some hardware

### Debugging / Contributing

To capture USB traffic between your MOZA device and PC for diagnosing issues or reverse-engineering new commands, see [USB Traffic Capture](docs/usb-capture.md).

### Settings Read/Write Flow

- **Read**: Plugin sends a read request; device responds with the current value
- **Write**: Plugin sends the new value; device applies it immediately
- **Polling**: Temperatures polled every 2 seconds; device settings read per-device after detection (not all at once on connect)
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
  TelemetrySender.cs               Multi-tier game data streaming to dashboard display
  TelemetryFrameBuilder.cs         Assembles bit-packed telemetry frames
  TelemetryEncoder.cs              Encodes game values to compressed wire formats
  TelemetryBitWriter.cs            LSB-first variable-width bit packer
  GameDataSnapshot.cs              Flat snapshot of SimHub game data for frame building
  DashboardProfile.cs              Channel definitions, tiers, and SimHubField enum
  DashboardProfileStore.cs         Parses .mzdash files and maps channels to SimHub fields
  TelemetryDiagnostics.cs          Frame logging and test pattern generation
Data/
  Telemetry.json                   150+ telemetry channel definitions (embedded resource)
Devices/
  MozaDeviceExtensionFilter.cs     Attaches extensions to MOZA devices in SimHub's device system
  MozaDeviceConstants.cs           Per-model GUIDs, GUID-to-model-prefix mapping, LED counts
  WheelModelInfo.cs                Per-model LED layout — button count, flag presence, index remapping
  MozaWheelDeviceExtension.cs      Wheel device extension — profiles, LED driver injection
  MozaWheelExtensionSettings.cs    Wheel settings for SimHub device profiles (includes telemetry)
  MozaWheelSettingsControl.xaml(.cs) Wheel device tab — LED config, dashboard telemetry, paddle settings
  MozaLedDeviceManager.cs          Virtual wheel LED driver — spoofs connection, forwards LED colors
  MozaDashDeviceExtension.cs       Dashboard device extension — profiles, LED driver injection
  MozaDashExtensionSettings.cs     Dashboard settings for SimHub device profiles
  MozaDashSettingsControl.xaml(.cs) Status panel for the dashboard device extension tab
  MozaDashLedDeviceManager.cs      Virtual dashboard LED driver — spoofs connection, forwards bitmask
DeviceTemplates/
  MozaWheelCSP/                    Per-model .shdp definitions (deployed at runtime on detection)
  MozaWheelKSP/                    Each contains a device.json with model-specific LED layout
  MozaWheelFSR2/                   Built as .shdp ZIPs and embedded in the DLL
  MozaWheelGSV2P/
  MozaWheelCSV21/
  MozaWheelGeneric/                Generic fallback for unknown new-protocol wheels
  MozaWheelOldProto/               Old-protocol (ES) wheels — RPM LEDs only
  MozaDashShdp/                    Dashboard definition (10 RPM + 6 flag LEDs)
UI/
  SettingsControl.xaml(.cs)        WPF settings UI (Base, Wheel, Options, Telemetry Diagnostics tabs)
  ColorPickerDialog.xaml(.cs)      RGB color picker dialog
  MozaPluginSettings.cs            Persisted plugin settings (brightness, timings, colors)
  MozaProfile.cs                   Per-game configuration snapshot (80+ settings)
  MozaProfileStore.cs              SimHub profile storage integration
```
