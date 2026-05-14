# Profile / Settings System Audit — 2026-05-14

Companion to the refactor plan in `refactor-profile-system-2026-05-14.md`. Field-level reference for the keying / gating / guard work.

All line numbers are against the working-tree state on `dev` at the time of audit. Re-verify with `grep` before quoting in code; the file is long and edits land often.

---

## Storage map

| Store | DTO | Persistence path | Keying | Lifetime |
|---|---|---|---|---|
| Plugin global | `MozaPluginSettings` (`UI/MozaPluginSettings.cs`) | SimHub `ReadCommonSettings`/`SaveCommonSettings("MozaPluginSettings")` → `PluginsData/MOZA Control/MozaPluginSettings.json` | Single file. Sub-maps key by wheel-model name or MCU-UID hex (see below). | Across SimHub launches. |
| Per-wheel-model slot | `PerWheelSlot` (`MozaPluginSettings.cs:472–502`) | Embedded in `MozaPluginSettings.PerWheelSlots` dict | Wheel-model name string (`StringComparer.OrdinalIgnoreCase`) | Across launches. Created on first wheel-model detection (`MozaPlugin.cs:2740–2743`). |
| Per-physical-wheel telemetry slot | `TelemetryWheelSlot` (`MozaPluginSettings.cs:460–465`) | `MozaPluginSettings.TelemetryByWheelUid` dict | 24-char lowercase MCU UID hex | Across launches. Populated on `wheel-mcu-uid` (`MozaPlugin.cs:2520–2584`). |
| Telemetry channel mappings | `Dictionary<string,Dictionary<string,Dictionary<string,string>>>` (`MozaPluginSettings.TelemetryChannelMappingsByWheel`) | Embedded in `MozaPluginSettings` | UID-hex → dashboard-key → channel-URL → property-path | Across launches. Legacy single-level `TelemetryChannelMappings` migrated by `MigrateLegacyChannelMappingsIfNeeded` (`MozaPluginSettings.cs:308–335`). |
| Per-wheel mzdash folder | `Dictionary<string,string>` (`MozaPluginSettings.WheelMzdashFolderByUid`) | Embedded in `MozaPluginSettings` | UID-hex → folder path | Across launches. |
| MOZA profile | `MozaProfile : ProfileBase` (`UI/MozaProfile.cs`) | SimHub `ProfileSettingsBase<MozaProfile, MozaProfileStore>` → `Profiles/*.shmozaprofile` (file filter `MozaProfileStore.cs:11`) | Profile name → game (SimHub maps games to profile names) | Across launches. |
| AB9 sub-profile | `Ab9Settings` (`MozaProfile.cs:14–52`) | Embedded under `MozaProfile.Ab9` | Same as parent profile. `null` until user touches AB9 panel. | Across launches. |
| Wheel page settings | `MozaWheelExtensionSettings` (`Devices/MozaWheelExtensionSettings.cs`) | SimHub device-extension JSON via `DeviceExtension.GetSettings/SetSettings` (`MozaWheelDeviceExtension.cs:176–195`) | SimHub stores per device-page instance (one JSON per `device.json` deployed). Plugin tags rows with `WheelModelName` field. | Across launches. |
| Dash page settings | `MozaDashExtensionSettings` (`Devices/MozaDashExtensionSettings.cs`) | Same mechanism as wheel ext (`MozaDashDeviceExtension.cs:117–136`) | Per dash device-page instance | Across launches. |
| Base ambient page settings | `MozaBaseExtensionSettings` (`Devices/MozaBaseExtensionSettings.cs`) | Same mechanism as wheel ext (`MozaBaseDeviceExtension.cs:119–138`) | Per base device-page instance | Across launches. |
| Wheel GUID registry | `Dictionary<string,string>` (`Devices/MozaDeviceConstants.cs:39–46, 59–66`) | `DevicesDefinitions/User/moza-wheel-guids.json` via `LoadRegistryFile`/`SaveRegistryFile` (`MozaDeviceConstants.cs:219–265`) | Wheel-model prefix string ↔ GUID | Across launches. Hardcoded backward-compat GUIDs at `MozaDeviceConstants.cs:39–46`; UUID v5 generation at `MozaDeviceConstants.cs:177–203`. |
| Device definition files | JSON (`device.json`) | `DevicesDefinitions/User/<DeviceName>/device.json` written by `DeviceDefinitionDeployer` | One folder per detected device | Across launches. Contains `DescriptorUniqueId` field SimHub matches to extension. |

Authoritative wheel-page-GUID resolution: `MozaDeviceConstants.GetWheelModelPrefix(deviceTypeId)` (`MozaDeviceConstants.cs:132–153`) and `MozaDeviceConstants.ResolveWheelGuid(modelPrefix)` (`MozaDeviceConstants.cs:110–123`). These are the helpers the refactor uses to convert between SimHub-side page identity and plugin-side model identity.

---

## A.1 — Field inventory

Symbols: `flat` = on `MozaPluginSettings` directly (current "active" state); `slot` = inside `PerWheelSlot`; `profile` = on `MozaProfile`; `wext` = `MozaWheelExtensionSettings`; `dext` = `MozaDashExtensionSettings`; `bext` = `MozaBaseExtensionSettings`; `tslot` = `TelemetryWheelSlot`; `global` = top-level plain field.

### A.1.1 — Wheel LED / mode / brightness (per-wheel-model)

| Field | Type | Defaults / sentinel | Read-from-disk | Write-to-disk | Hardware apply | Detection gate | UI mutation | Target classification |
|---|---|---|---|---|---|---|---|---|
| `WheelTelemetryMode` | int | `-1` flat / slot; brightness flat default `100` n/a here | `LoadSlotIntoActive` `MozaPluginSettings.cs:412–447` | `MirrorActiveToSlot` `MozaPluginSettings.cs:370–409`; `MozaWheelExtensionSettings.CaptureFromCurrent:74` | `ApplySavedWheelSettings` `MozaPlugin.cs:3003`; `WriteProfileWheelSettingsToDevice` `MozaPlugin.cs:3633`; `ApplyWheelExtensionSettings` `MozaPlugin.cs:3862` | `_newWheelDetected` | `MozaWheelSettingsControl.xaml.cs:836` | profile + wheel-overlay |
| `WheelIdleEffect` | int | `-1` | same | same | same `MozaPlugin.cs:3005`/`3636`/`3864` | `_newWheelDetected` | `:846` | profile + wheel-overlay |
| `WheelButtonsIdleEffect` | int | `-1` | same | same | same `:3007`/`3638`/`3866` | `_newWheelDetected` | `:857` | profile + wheel-overlay |
| `WheelKnobIdleEffect` | int | `-1` | same | same | same `:3009`/`3640`/`3868` | `_newWheelDetected` | `:868` | profile + wheel-overlay |
| `WheelKnobLedMode` | int | `-1` | same | same | same `:3011`/`3642`/`3870` | `_newWheelDetected` | `:932` | profile + wheel-overlay |
| `WheelButtonsLedMode` | int | `-1` | same | same | same `:3013`/`3644`/`3872` | `_newWheelDetected` | `:943` | profile + wheel-overlay |
| `WheelTelemetryIdleSpeedMs` | int | `-1` (UI defaults to 1000 ms display) | same | same | `WriteArray("wheel-telemetry-idle-interval")` `MozaPlugin.cs:3019`/`3646`/`3874` (paired with idle effect) | `_newWheelDetected` | `:886` | profile + wheel-overlay |
| `WheelButtonsIdleSpeedMs` | int | `-1` | same | same | same paired write `:3022`/`3649`/`3877` | `_newWheelDetected` | `:899` | profile + wheel-overlay |
| `WheelKnobIdleSpeedMs` | int | `-1` | same | same | same `:3025`/`3652`/`3880` | `_newWheelDetected` | `:912` | profile + wheel-overlay |
| `WheelSleepMode` | int | `-1` | same | same | `wheel-idle-mode` `:3029`/`3655`/`3883` | `_newWheelDetected` | `:955` | profile + wheel-overlay |
| `WheelSleepTimeoutMin` | int | `-1` | same | same | `wheel-idle-timeout` `:3031`/`3657`/`3885` | `_newWheelDetected` | `:968` | profile + wheel-overlay |
| `WheelSleepSpeedMs` | int | `-1` | same | same | `WriteArray("wheel-idle-speed")` `:3033`/`3659`/`3887` | `_newWheelDetected` | `:972` | profile + wheel-overlay |
| `WheelSleepColor` | `int[]?` (1) | `null` | same | same | `WriteColor("wheel-idle-color")` `:3038`/`3664`/`3892` | `_newWheelDetected` | `:1000` (`WheelSleepColorSwatch_Click`) | profile + wheel-overlay |
| `WheelRpmBrightness` | int | flat `100` / slot `100` (no sentinel) | same | same | **unconditional** `_deviceManager.WriteSetting("wheel-rpm-brightness", _settings.WheelRpmBrightness)` `MozaPlugin.cs:3048` (no `>= 0` guard) | `_newWheelDetected` | profile-side `:3667` guarded | profile + wheel-overlay |
| `WheelButtonsBrightness` | int | flat `100` / slot `100` | same | same | **unconditional** `:3049` | `_newWheelDetected` | profile-side `:3669` guarded | profile + wheel-overlay |
| `WheelFlagsBrightness` | int | flat `100` / slot `100` | same | same | `:3054` (gated `_dashDetected`) | `_newWheelDetected` + `_dashDetected` | profile-side `:3672` | profile + wheel-overlay |
| `WheelESRpmBrightness` | int | flat `15` / slot `15` | same | same | **unconditional** `:3055` | `_oldWheelDetected` | profile-side `:3683` | profile + wheel-overlay |
| `WheelRpmIndicatorMode` | int | `-1` | same | same | `wheel-rpm-indicator-mode` (+1 mapping) `:3043`/`3679` | `_oldWheelDetected` | `:1036` | profile + wheel-overlay |
| `WheelRpmDisplayMode` | int | `-1` | same | same | `wheel-set-rpm-display-mode` `:3045`/`3681` | `_oldWheelDetected` | `:1046` | profile + wheel-overlay |
| `WheelPaddlesMode` | int | `-1` | flat (load via slot) | flat (mirror via slot) | UI direct `SettingsControl.xaml.cs:659` (no detection check) | none in UI handler | `SettingsControl.xaml.cs:659` | profile + wheel-overlay |
| `WheelClutchPoint` | int | `-1` | same | same | UI direct `:670` | none in UI handler | `:670` | profile + wheel-overlay |
| `WheelKnobMode` | int | `-1` | same | same | UI direct `:680` | none in UI handler | `:680` | profile + wheel-overlay |
| `WheelStickMode` | int | `-1` | same | same | UI direct `:709`/`:719` | none in UI handler | `:709`/`:719` | profile + wheel-overlay |
| `WheelKnobBackgroundColors` | `int[]?` | `null` | same | same | `WriteKnobColors` `MozaPlugin.cs:3770–3806` (per-LED writes) | `_newWheelDetected` + `KnobCount>0` model gate + `IsWheelLedGroupPresent(3)` | `MozaWheelSettingsControl.xaml.cs:404`/`:534` | profile + wheel-overlay |
| `WheelKnobPrimaryColors` | `int[]?` | `null` | same | same | `WriteKnobColors` `MozaPlugin.cs:3777–3785` (`wheel-knob{N}-active-color`) | same | `:534` | profile + wheel-overlay |
| `WheelKnobRingColors` | `int[]?` (≤56) | `null` | same | same | `WriteKnobRingColors` `MozaPlugin.cs:3814–3827` (per-LED `wheel-knob-bg-color{N}`) | same | `MozaWheelSettingsControl.xaml.cs:404` | profile + wheel-overlay |
| `WheelKnobRingBrightness` | int | `-1` | same | same | `wheel-knob-brightness` `MozaPlugin.cs:3819` | same | `MozaWheelSettingsControl.xaml.cs:505` | profile + wheel-overlay |
| `WheelRpmBlinkColors` | `int[]?` (10) | `null` | flat (NOT in slot's mirror? — checked: `MirrorActiveToSlot:394` does include it) | same | `WriteColorArray` `MozaPlugin.cs:3707`/`3915` (`wheel-rpm-blink-color{N}`) | `_newWheelDetected` | UI via color picker in main control | profile + wheel-overlay |

### A.1.2 — Dash / display (single dash page; profile-only)

| Field | Type | Defaults / sentinel | Read-from-disk | Write-to-disk | Hardware apply | Detection gate | UI mutation | Target classification |
|---|---|---|---|---|---|---|---|---|
| `DashRpmBrightness` | int | `100` flat | direct field | `MirrorActiveToSlot` does NOT cover it (truly global); `DashExtensionSettings.CaptureFromCurrent:36` | `ApplySavedDashSettings` `MozaPlugin.cs:3072`; profile path `:3691`; ext path `:3977` | `_dashDetected` | `MozaDashSettingsControl.xaml.cs:312` | profile-only |
| `DashFlagsBrightness` | int | `100` flat | same | same | `:3073`/`:3693`/`:3979` | `_dashDetected` | `:324` | profile-only |
| `DashDisplayBrightness` | int | `100` flat | same | same | `_telemetrySender?.SendDashDisplayBrightness` `:3077`/`:3695`/`:3981` | `_dashDetected` + telemetry sender alive | `MozaWheelSettingsControl.xaml.cs:1231` (lives on wheel control, debounced 500 ms) | profile-only |
| `DashDisplayStandbyMin` | int | `5` flat | same | same | `SendDashDisplayStandbyMinutes` `:3078`/`:3697`/`:3983` | `_dashDetected` | `MozaWheelSettingsControl.xaml.cs:1243` | profile-only |
| `DashRpmBlinkColors` | `int[]?` (10) | `null` | direct field | direct field | `WriteColorArray` `:3731`/`:3993` | `_dashDetected` (`!DashDeviceExtensionActive` for profile path) | UI via color picker in dash control | profile-only |
| `DashRpmColors` (in `MozaData` + `profile.DashRpmColors`) | `byte[][]` / `int[]?` | n/a / `null` | profile carries packed array `MozaProfile.cs:163` | `MozaProfile.CaptureFromCurrent:361` | `WriteColorArray("dash-rpm-color")` `:3730`/`:3992` | `_dashDetected` | `MozaDashSettingsControl.xaml.cs:163`/`:193` | profile-only |
| `DashFlagColors` | same | same | profile `:165` | `:363` | `:3732`/`:3994` | `_dashDetected` | `:163` | profile-only |
| `DashRpmIndicatorMode` | int | n/a in `MozaPluginSettings` (lives on `_data` + dext) | dext `:75` | dext capture `:40` | `dash-rpm-indicator-mode` `:3985` (ext path), `:3084` (saved-dash path writes constant `1` to enable flags-indicator) | `_dashDetected` | `MozaDashSettingsControl.xaml.cs:282` | profile-only |
| `DashFlagsIndicatorMode` | int | n/a / dext | same | same | `:3987` | `_dashDetected` | `:302` | profile-only |
| `DashRpmDisplayMode` | int | n/a / dext | same | same | `:3989` | `_dashDetected` | `:291` | profile-only |

### A.1.3 — Base motor / FFB (profile-only)

| Field | Type | Defaults / sentinel | Hardware apply | Detection gate | UI mutation | Target classification |
|---|---|---|---|---|---|---|
| `Limit`, `FfbStrength`, `Torque`, `Speed`, `Damper`, `Friction`, `Inertia`, `Spring`, `SpeedDamping`, `SpeedDampingPoint`, `NaturalInertia`, `SoftLimitStiffness`, `SoftLimitRetain`, `FfbReverse`, `Protection` | int (profile only) | `-1` profile sentinel | `ApplyBaseSettingIfSet` `MozaPlugin.cs:3587–3596` (gates `_data.IsBaseConnected`) | `_data.IsBaseConnected` | `SettingsControl.xaml.cs:340–572` | profile-only |
| `GameDamper`, `GameFriction`, `GameInertia`, `GameSpring`, `WorkMode` | int (profile only) | `-1` | same | same | `:469`/`:480`/`:491`/`:502`/`:581` | profile-only |
| `Equalizer1..6` | int (profile only) | `-1000` (different sentinel) | `ApplyEq` lambda `MozaPlugin.cs:3439` (inline `IsBaseConnected` gate) | same | `:874–879` | profile-only |
| `FfbCurveY1..5` | int (profile only) | `-1` | `:3458–3467` (X1..X4 written unconditionally; Y values from profile or fall back to `_data`) | `_data.IsBaseConnected` | `:916–920` | profile-only |
| `HandbrakeMode/ButtonThreshold/Direction/Min/Max` | int (profile only) | `-1` | `ApplyHandbrakeSettingIfSet` `MozaPlugin.cs:3598–3604` (gates `_handbrakeDetected`) | `_handbrakeDetected` | `:760–780` | profile-only |
| `HandbrakeCurve[5]` | `int[]?` (profile only) | `null` | `ApplyProfile` inline `MozaPlugin.cs:3475–3483` (gates `_handbrakeDetected`); also `ApplySavedHandbrakeSettings:3165–3172` (writes unconditionally inside the method — caller-gated) | `_handbrakeDetected` | n/a in current UI | profile-only |
| `PedalsThrottleDir/BrakeDir/ClutchDir`, `PedalsBrakeAngleRatio`, `PedalsThrottleCurve/BrakeCurve/ClutchCurve` | int / `int[]?` (profile only) | `-1` / `null` | `ApplyPedalSettingIfSet` `MozaPlugin.cs:3606–3612` + `ApplyCurveIfSet` `:3614–...` (gates `_pedalsDetected`) | `_pedalsDetected` | `:1115–1155` | profile-only |
| `Ab9` | `Ab9Settings?` (profile only) | `null` (intentional) | `_ab9Manager.SendMode/SendSlider/SendGearShiftVibrationIntensity` `MozaPlugin.cs:3542–3551` (inside `if (profile.Ab9 != null && _ab9Detected)` ✓ guard present); separate `ApplySavedAb9Settings` `:2454–2477` ✓ guarded | `_ab9Detected` + `profile.Ab9 != null` | `UI/SettingsControl.xaml.cs:2406`/`:2440`/`:2477` (per-slider UI) | profile-only |

### A.1.4 — Base wheelbase ambient LED (profile-only)

| Field | Type | Defaults / sentinel | Hardware apply | Detection gate | UI mutation | Target classification |
|---|---|---|---|---|---|---|
| `BaseAmbientBrightness` | int (flat default `100`; bext sentinel `-1`) | `100` / `-1` | `ApplySavedBaseAmbientSettings:3105`; `ApplyBaseExtensionSettings:4013` | `_baseAmbientLedSupported` | `MozaBaseSettingsControl.xaml.cs:200` | profile-only |
| `BaseAmbientStandbyMode` | int (flat `4` / bext `-1`) | `4` / `-1` | `:3106`/`:4015` | same | `:178` | profile-only |
| `BaseAmbientIndicatorState` | int (flat `1` / bext `-1`) | `1` / `-1` | `:3107`/`:4017` | same | `:166` | profile-only |
| `BaseAmbientSleepMode` | int (flat `1` / bext `-1`) | `1` / `-1` | `:3108`/`:4019` | same | `:189` | profile-only |
| `BaseAmbientSleepTimeout` | int (flat `15` / bext `-1`) | `15` / `-1` | `:3109`/`:4021` | same | `:211` | profile-only |
| `BaseAmbientStartupColor` | int packed RGB (flat `0x66B8FF` / bext `-1`) | `0x66B8FF` / `-1` | `WritePackedColor` `:3110`/`:4023` | same | `:218` | profile-only |
| `BaseAmbientShutdownColor` | int packed RGB (flat `0x66B8FF` / bext `-1`) | `0x66B8FF` / `-1` | `:3111`/`:4025` | same | `:224` | profile-only |

`MozaProfile` currently has NO base-ambient fields. Adding them is part of the refactor (B.1).

### A.1.5 — Telemetry / dashboard plumbing

| Field | Current home | Default | Current key | Apply site | UI mutation | Target classification |
|---|---|---|---|---|---|---|
| `TelemetryEnabled` | flat global + `tslot.TelemetryEnabled` | `false` | global / per-UID slot | `ApplyTelemetrySettings` (`MozaPlugin.cs:1151+`); per-UID load `:2520–2584` | `MozaWheelSettingsControl.xaml.cs:1185` | profile + wheel-overlay (move to `WheelOverride`) |
| `TelemetryProfileName` | flat global + `tslot.TelemetryProfileName` | `""` | same | same | `:65–66` (DashboardSelectionChanged event) | profile + wheel-overlay |
| `TelemetryMzdashPath` | flat global + `tslot.TelemetryMzdashPath` | `""` | same | same | telemetry UI | profile + wheel-overlay |
| `TelemetryMzdashFolder` | flat global | `""` | global | runtime + auto-detect on UID | UI | plugin-global (unchanged) |
| `WheelMzdashFolderByUid` | `Dictionary<string,string>` | empty | UID hex | `DetectDevices:2501–2513` | Auto-detect button (`MozaWheelSettingsControl.xaml.cs:1420`) | profile + wheel-overlay (rekey to page GUID) |
| `TelemetryByteLimitOverride` | flat global | `0` | global | `ApplyTelemetrySettings` | UI | plugin-global |
| `TelemetryUploadDashboard` | flat global | `false` | global | same | UI | plugin-global |
| `TelemetryDownloadDashboard` | flat global | `false` | global | same | UI | plugin-global |
| `TelemetryProtocolVersion` (legacy migrated) | flat global | `-1` | global, drained on load | `ApplyTelemetrySettings` migration | none | plugin-global (transient) |
| `TelemetryFirmwareEraLegacy` (legacy) | flat global | `-1` | global, drained on load | `ApplyTelemetrySettings` migration | none | plugin-global (transient) |
| `TelemetryWheelEra` | flat global | `MozaWheelEra.Auto` | global | `ApplyTelemetrySettings` | UI dropdown | profile + wheel-overlay (era is wheel-specific) |
| `TelemetrySendRateHz` | flat global | `20` | global | runtime | UI | plugin-global |
| `TelemetrySendModeFrame` | flat global | `true` | global | runtime | UI | plugin-global |
| `TelemetrySendSequenceCounter` | flat global | `true` | global | runtime | UI | plugin-global |
| `TelemetryChannelMappings` (legacy) | flat global | `null` | dashboard-key → channel → path | one-shot migration `MigrateLegacyChannelMappingsIfNeeded` `MozaPluginSettings.cs:308–335` | none | **delete after migration** |
| `TelemetryChannelMappingsByWheel` | `Dictionary<UID, Dictionary<dashKey, Dictionary<channel, path>>>` | empty | UID → dashboard-key → channel | `MozaPlugin.cs:1588`, `:1623`, `:1711` (in `ApplyTelemetrySettings` and surrounding routines) | telemetry mapping UI (`MozaWheelSettingsControl.xaml.cs:1619` reset, plus per-mapping picker elsewhere) | **profile × wheel-page-GUID × dashboard-key × channel** (see refactor B.2) |
| `TelemetryDashboardKey` | profile only | `null` | per-profile | `ApplyTelemetryDashboardFromProfile` (called from `ApplyProfile:3564`) | captured in `SaveSettings:928–935` | profile-only (already correct) |

### A.1.6 — Plugin-global toggles (truly global)

| Field | Default | Notes | Classification |
|---|---|---|---|
| `ConnectionEnabled` | `true` | Master kill-switch | plugin-global |
| `LastWheelbasePort`, `LastAb9Port` | `""` | Connection-cache hints | plugin-global |
| `DisableSerialProbeFallback` | `false` | User opt-out for non-MOZA-port probing | plugin-global |
| `DisableAb9Detection` | `false` | User opt-out + auto-disable on Wine | plugin-global |
| `AutoApplyProfileOnLaunch` | `true` | Controls whether `InitProfileSystem` applies profile on connect | plugin-global |
| `LimitWheelUpdates` | `false` | Per-wheel-firmware workaround | plugin-global (debatable — could be wheel-overlay; deferring) |
| `ExtLedDiagMin[6]`, `ExtLedDiagMax[6]` | `-1` × 6 | Diagnostic-panel state | page-state for the diagnostics panel (low priority) |
| `WheelKeepalive` | `true` | LED resend tick for ES wheels | plugin-global (or wheel-overlay) |
| `AlwaysResendBitmask` | `false` | Wheel-firmware workaround | plugin-global (or wheel-overlay) |
| `GearshiftVibrateOnNeutral` | `false` | Plugin-side gearshift tuning | profile-only |
| `GearshiftDebounceMs` | `500` | Plugin-side gearshift tuning | profile-only |
| `StartCaptureOnNextLaunch` | `false` | One-shot diagnostic arm | plugin-global (transient) |
| `EnableWireTraceFileSink` (`[JsonIgnore]`) | `false` | Code-only toggle | not persisted |
| `EnableAutoTestOnConnect` (`[JsonIgnore]`) | `false` | Code-only toggle | not persisted |
| `AutoTestLastSlot` | `-1` | Auto-test session state | plugin-global |
| `ProfileStore` | new `MozaProfileStore()` | SimHub-managed | plugin-global |

---

## A.2 — Cold-start / new-user guard gaps

Ordered by severity. Each entry: location, what's wrong, current behaviour, recommended fix in the refactor.

### A.2.1 — `ApplySavedWheelSettings` brightness writes lack sentinel guards

**Location**: `MozaPlugin.cs:3048–3055`.

```
_deviceManager.WriteSetting("wheel-rpm-brightness", _settings.WheelRpmBrightness);
_deviceManager.WriteSetting("wheel-buttons-brightness", _settings.WheelButtonsBrightness);
if (_dashDetected)
    _deviceManager.WriteSetting("dash-flags-brightness", _settings.WheelFlagsBrightness);
_deviceManager.WriteSetting("wheel-old-rpm-brightness", _settings.WheelESRpmBrightness);
```

`WheelRpmBrightness` / `WheelButtonsBrightness` default to `100`, `WheelESRpmBrightness` defaults to `15` — so a fresh install will push `100/100/15` to the wheel on every cold start, overriding whatever the wheel had set last time. Unlike every other field in this method, no `>= 0` sentinel guard exists. The matching profile-path writes (`MozaPlugin.cs:3666–3672`/`:3683`) DO guard.

Fix: introduce `-1` sentinel on the flat field too (or just guard in the apply site).

### A.2.2 — UI handlers push to hardware without detection check

The following handlers call `_device.WriteSetting`/`WriteColor` immediately after the suppressor + null-plugin guard, with no detection-flag precheck. If the user moves a slider while the matching device is absent, the write queues against nothing.

- **Main settings panel (`UI/SettingsControl.xaml.cs`)**:
  - Wheel paddles mode `:659`, clutch point `:670`, knob mode `:680`, knob signal `:688`, stick mode `:709`/`:719`. No `_newWheelDetected` check.
  - Handbrake mode `:760`, threshold `:770`, direction `:780`. No `_handbrakeDetected` check.
  - FFB equalizer 1–6 `:874–879`. No `_data.IsBaseConnected` check.
  - FFB curve Y1–Y5 `:916–920`. No `IsBaseConnected` check.
  - Pedals throttle/brake/clutch dir, min/max, curves `:1115–1155`. No `_pedalsDetected` check.
  - Ext-LED diag colors / brightness / mode `:2131`/`:2144`/`:2158`/`:2185`/`:2201`/`:2209`. No detection check.
  - AB9 mode / sliders / gear-shift `:2406`/`:2440`/`:2477`. No `_ab9Detected` check at the handler level (manager does internal null-check, but pushes to an absent device buffer queue if connected later).
- **Wheel settings panel (`Devices/MozaWheelSettingsControl.xaml.cs`)**:
  - Knob ring brightness `:505`, color swatches `:404`/`:534`. No `_newWheelDetected` check.
  - All telemetry/idle effect combos / sleep mode / ES indicator `:836–1046`. No `_newWheelDetected` check.
- **Dash settings panel (`Devices/MozaDashSettingsControl.xaml.cs`)**:
  - Indicator combos `:282`/`:291`/`:302`, brightness sliders `:313`/`:324`, color swatches `:163`/`:193`. No `_dashDetected` check (except the `Refresh()` UI-state gating).
- **Base ambient panel (`Devices/MozaBaseSettingsControl.xaml.cs`)**:
  - Indicator / standby / sleep combos `:166`/`:178`/`:189`, brightness/timeout sliders `:200`/`:211`, color swatches `:218`/`:224`. The control's UI gates visibility on `_baseAmbientLedSupported` (via `LinkedLedDriver.IsConnected()`), but the handlers themselves don't double-check.

Fix: introduce an `IfDetected(flag, action)` helper that wraps `_device.WriteSetting`/`WriteColor`. When the flag is false, only update profile/overlay + persist — skip the hardware write. The UI side panels are already structured to hide on disconnect, so this is a defence-in-depth measure for handlers that can still fire on rapid disconnect/reconnect.

### A.2.3 — `MigrateLegacyChannelMappingsIfNeeded` collapses to `""` UID key

**Location**: `MozaPluginSettings.cs:308–335`, invoked at `MozaPlugin.cs:419`.

Legacy single-level mapping is moved into `TelemetryChannelMappingsByWheel[""]` (the empty-UID slot). This is intentional ("user just installed the build, no wheel had been UID-bound yet"), but it leaves the mappings under a UID key that's never the actual UID, so they only resolve when the active UID lookup falls through to `""`. The refactor (B.2) re-keys this entirely; the migration path needs an analogous translation to per-profile / per-page-GUID — currently a user who upgrades while no wheel is connected gets mappings stranded under `""`.

### A.2.4 — UID-keyed dictionaries depend on UID arriving before save

**Location**: `MozaPlugin.cs:944–954` in `SaveSettings`.

The save handler guards with `if (uid != null && uid.Length == 12)` before writing to `TelemetryByWheelUid`. Good. But the same guard does NOT protect against `SaveSettings` being called early in a session when only the wheel's MCU UID has arrived but `wheel-model-name` hasn't — fine for telemetry slot, but means there's no cross-keying with the wheel-page-GUID until much later. After the refactor (page-GUID keyed), the page GUID is known the moment the device-extension instantiates (from the SimHub `DeviceTypeID`), so persistence can happen earlier and without the UID round-trip.

### A.2.5 — `MozaProfile.CaptureFromCurrent` skips if `BaseSettingsRead` false

**Location**: `MozaProfile.cs:281`. Same pattern in `MozaWheelExtensionSettings.cs:71` and `MozaBaseExtensionSettings` (does NOT — actually `MozaBaseExtensionSettings.CaptureFromCurrent` has no guard; `MozaDashExtensionSettings.cs:34` has one).

`if (!data.BaseSettingsRead) return;` silently produces an empty / partial profile capture if SaveSettings runs before base settings have been read back. The downstream `ApplyProfile` then has a zero-detect guard (`MozaPlugin.cs:3270–3281`) that resets to sentinels — but this only catches the all-zero base block, not partial captures.

Recommended: keep the guard but log a warn so we can correlate "captured empty profile" with user-visible "settings disappeared" reports. The refactor's "single source of truth" approach (profile, not `_settings` + `_data` doubly) eliminates the capture problem entirely; UI writes the value into profile directly.

### A.2.6 — `MozaProfile.UnpackColorsInto` silently truncates on size mismatch

**Location**: `MozaProfile.cs:391–402`.

```
int count = Math.Min(packed.Length, target.Length);
for (int i = 0; i < count; i++) { ... }
```

If a stored profile has fewer colors than the wheel exposes, the tail of the target array keeps its previous values (which could be from a prior wheel model). Not a crash, but a visible bug after a wheel swap if the user had per-wheel-specific colour customisations on the smaller wheel and then connects a larger one.

Fix: when the source is shorter than the target, fill the tail with the default colour (typically `0x00`/`0xFF` per LED type) rather than leaving stale residue. Easy to do after the refactor consolidates colours into the wheel overlay.

### A.2.7 — Flat `Wheel*` fields can be torn-read across threads

Already covered in `todo.md`'s "MozaPluginSettings._slotsLock doesn't cover flat Wheel* properties" item — flat fields are `volatile int` (atomic on .NET) but the slot mirror happens under `_slotsLock` while reads happen outside. The refactor removes the flat fields entirely (single source of truth = profile + overlay), so this resolves naturally.

### A.2.8 — `WheelRpmBlinkColors` write-only with no readback round-trip safety

`WheelRpmBlinkColors` and `DashRpmBlinkColors` are write-only on the wire (`MozaPluginSettings.cs:97–98` comments). The flat fields are the only persistent copy. If `MozaWheelExtensionSettings.ApplyTo` writes `null` over them (legacy JSON), the user's custom blink palette disappears. The current code guards null at `MozaWheelExtensionSettings.cs:143` (sleep color) but `WheelRpmBlinkColors` flows through the slot/flat copy on a different path — it gets overwritten regardless of null.

Spot-check during refactor.

### A.2.9 — `MozaWheelDeviceExtension.SetSettings` runs for inactive wheel extensions

**Location**: `MozaWheelDeviceExtension.cs:185–195`, mitigation explained at `MozaPlugin.cs:3932–3956` (the comment block — preserved here for context).

SimHub invokes `SetSettings` on every registered device extension at startup, not just the one whose wheel is currently connected. The mitigation is the `modelMatches` gate in `ApplyWheelExtensionSettings` (`MozaPlugin.cs:3849`). This is the bug-prevention reason the keying needs to be tight; the refactor's page-GUID overlay store eliminates the bleed entirely (each `SetSettings` writes only to its own overlay slot, not the active hardware path).

---

## A.3 — Per-event walkthroughs

### A.3.1 — Cold start (plugin Init, hardware not yet connected)

1. `MozaPlugin.Init` (`MozaPlugin.cs:383`) — ✓ disposes prior state, resets detection flags.
2. `_settings = ReadCommonSettings<MozaPluginSettings>` (`:409`) — ✓ deserialises plugin settings.
3. Null-guard for missing `ProfileStore` (`:412`).
4. `MigrateLegacyChannelMappingsIfNeeded` (`:419`) — ⚠ migrates into `""` UID slot (see A.2.3).
5. `UnpackColorsInto` blink colours (`:427–428`) — ✓ restores write-only palettes.
6. Device managers / serial connection / timers come up (lines ~456+).
7. `InitProfileSystem` (`:3215`) — ✓ creates default profile if missing, hooks `CurrentProfileChanged`, applies initial profile if `AutoApplyProfileOnLaunch`. ⚠ but at this point NO hardware is connected, so all `Apply*SettingIfSet` calls inside `ApplyProfile` skip the hardware write. The `_settings` and `_data` get pre-populated.
8. SimHub instantiates device extensions and calls `SetSettings(json, isDefault=false)` for each. Each calls into `ApplyWheelExtensionSettings` / `ApplyDashExtensionSettings` / `ApplyBaseExtensionSettings`. ⚠ runs before any hardware detection: writes flat fields + slot + (gated on detection) hardware. Currently safe because of detection gates and the `modelMatches` check, but the model-match check assumes `_data.WheelModelName` is set — which isn't true at this point, so `writeFlat` is true and `writeHardware` is false. Slot stays empty for wheels not yet matched.
9. User plugs hardware. `MozaSerialConnection.OnMessageReceived` → `DetectDevices`.
10. `wheel-mcu-uid` arrives (`:2490+`) — ⚠ Per-UID telemetry slot load. ⚠ Auto-mzdash-folder switch.
11. `base-mcu-temp` arrives (`:2607`) — ✓ triggers `ApplyProfile(CurrentProfile)`. Base settings flow.
12. `wheel-telemetry-mode` arrives (`:2666`) — ✓ triggers `ApplySavedWheelSettings` (⚠ unconditional brightness — A.2.1) + identity reads.
13. `wheel-model-name` arrives (`:2707`) — ✓ if first time: `LoadSlotIntoActive(currentModel)` or `MirrorActiveToSlot` (seed), `DeployForModel`, knob colors, `StartTelemetryIfReady`. ⚠ key is model-name string; refactor changes to page GUID.
14. `dash-rpm-indicator-mode` arrives (`:2641`) — ✓ `_dashDetected=true`, `ApplySavedDashSettings` ⚠ also has `WriteSetting("dash-flags-indicator-mode", 1)` unconditionally (`:3084`) which is forced behaviour and probably correct but worth flagging.
15. `handbrake-direction` arrives (`:2899`) — ✓ `ApplySavedHandbrakeSettings` (profile-driven, all guarded).
16. `pedals-throttle-dir` arrives (`:2909`) — ✓ `ApplySavedPedalSettings` (profile-driven, all guarded).
17. `base-ambient-brightness` arrives (`:2653`) — ✓ `_baseAmbientLedSupported=true`, `ApplySavedBaseAmbientSettings`. ⚠ writes ALL fields unconditionally (no `>= 0` guards on lines `:3105–3111`), because the flat defaults are `100`/`4`/`1`/`1`/`15`/`0x66B8FF`/`0x66B8FF` (all "intended initial state"). For fresh install this is what we want; if the user has tweaked settings AND the wheel was disconnected mid-session, the saved values apply.

### A.3.2 — Game / profile switch

1. SimHub fires `MozaProfileStore.CurrentProfileChanged` when the active game changes.
2. `OnProfileChanged` (`MozaPlugin.cs:3250`) — ✓ resolves `CurrentProfile`, calls `ApplyProfile`.
3. `ApplyProfile` (`:3263`):
   - ✓ Zero-detect guard at `:3270–3281`.
   - ✓ Base/motor settings → guarded by `IsBaseConnected` via `ApplyBaseSettingIfSet`.
   - ⚠ Wheel LED block at `:3312–3413` mutates `_settings` (flat fields) AND `_data` even when no wheel detected. Hardware write is later gated by `WriteProfileWheelSettingsToDevice` `:3625` → `_newWheelDetected`/`_oldWheelDetected`. The `_settings` mutation is the source-of-truth bug the refactor fixes — currently if the user has wheel A connected, switches profile, then swaps to wheel B, the flat fields still hold profile-A's values for wheel A's overlay slot until the next slot-load.
   - ✓ FFB Eq guards `_data.IsBaseConnected`. ⚠ FFB Curve X breakpoints write unconditionally when base connected (`:3452–3456`).
   - ✓ Handbrake / pedals guarded.
   - ✓ AB9 guarded `profile.Ab9 != null && _ab9Detected` (`:3539`).
   - ✓ Persists + queues telemetry dashboard re-apply.

### A.3.3 — Wheel swap

1. `PollStatus` (`MozaPlugin.cs:2140`) reads `wheel-model-name` periodically.
2. New model detected at `:2719–2725` → `ResetWheelDetection`.
3. `ResetWheelDetection` (`:2120`) — ✓ stops telemetry, clears flags, clears `WheelModelInfo`, `_data.ClearWheelIdentity`, `_lastKnownWheelModel = ""`.
4. Next probe response re-enters `DetectDevices` as cold start — but with `_settings` flat fields still holding the PREVIOUS wheel's values (because flat fields are the active state, not the new wheel's slot).
5. `wheel-model-name` for new wheel arrives → `LoadSlotIntoActive(newModel)` swaps the flat fields. ✓ But there's a race: `ApplySavedWheelSettings` ran BEFORE `wheel-model-name` (at the `wheel-telemetry-mode` branch — see A.3.1 step 12). So the new wheel briefly receives the OLD wheel's brightness/modes before `LoadSlotIntoActive` runs. The refactor pushes this gate so hardware writes wait until the page GUID is resolved.

### A.3.4 — User edits setting

1. UI event handler fires (e.g. `MozaWheelSettingsControl.xaml.cs:836` for telemetry mode).
2. ⚠ Handler updates `_data.X` AND `_settings.X` directly.
3. ⚠ Handler calls `_device.WriteSetting(...)` without detection check (see A.2.2).
4. ✓ Handler calls `_plugin.SaveSettings()` (`MozaPlugin.cs:923`).
5. `SaveSettings`:
   - ✓ Resolves active dashboard key.
   - ✓ Captures `_data` + `_settings` into `CurrentProfile` via `CaptureFromCurrent`.
   - ✓ Mirrors flat → slot for active wheel model.
   - ✓ Saves UID telemetry slot if UID known.
   - ✓ `ScheduleSave` (500 ms debounce).
6. Debounce fires → `SaveCommonSettings("MozaPluginSettings", _settings)`.
7. Device-extension UI path: SimHub may also call `GetSettings()` later for serialisation; `MozaWheelDeviceExtension.GetSettings` runs `_settings.CaptureFromCurrent(plugin.Settings, plugin.Data)` first — pulling fresh flat values into the extension blob.

---

## A.4 — Classification summary

### Move to `profile-only` (drop flat / drop slot / drop extension persistence)

- All `MozaProfile` motor/FFB/handbrake/pedals fields (already there).
- AB9 (already there).
- `BaseAmbient*` (currently flat-only on `MozaPluginSettings` + bext duplicate; ADD to `MozaProfile`, drop flat).
- `Dash*` brightness / indicator modes / colors / `DashRpmBlinkColors` (currently flat + dext duplicate; consolidate to profile).
- `GearshiftVibrateOnNeutral`, `GearshiftDebounceMs` (currently flat; move to profile).

### Move to `profile + wheel-overlay` keyed by `DescriptorUniqueId`

All current `MozaWheelExtensionSettings` fields and the matching flat-field set on `MozaPluginSettings`:

- `WheelTelemetryMode`, `WheelIdleEffect`, `WheelButtonsIdleEffect`, `WheelKnobIdleEffect`, `WheelKnobLedMode`, `WheelButtonsLedMode`, `WheelTelemetryIdleSpeedMs`, `WheelButtonsIdleSpeedMs`, `WheelKnobIdleSpeedMs`.
- `WheelSleepMode`, `WheelSleepTimeoutMin`, `WheelSleepSpeedMs`, `WheelSleepColor`.
- `WheelRpmBrightness`, `WheelButtonsBrightness`, `WheelFlagsBrightness`, `WheelESRpmBrightness`.
- `WheelRpmIndicatorMode`, `WheelRpmDisplayMode`.
- `WheelPaddlesMode`, `WheelClutchPoint`, `WheelKnobMode`, `WheelStickMode`.
- `WheelKnobBackgroundColors`, `WheelKnobPrimaryColors`, `WheelKnobRingColors`, `WheelKnobRingBrightness`.
- `WheelRpmBlinkColors`, `WheelRpmColors`, `WheelButtonColors`, `WheelButtonDefaultDuringTelemetry`, `WheelIdleColor`, `WheelESRpmColors`, `WheelFlagColors`.
- Telemetry per-wheel: `TelemetryEnabled`, `TelemetryProfileName`, `TelemetryMzdashPath`, `TelemetryWheelEra`, `WheelMzdashFolder` (rekey from UID).

### Stay plugin-global

- `ConnectionEnabled`, `LastWheelbasePort`, `LastAb9Port`.
- `DisableSerialProbeFallback`, `DisableAb9Detection`, `AutoApplyProfileOnLaunch`, `LimitWheelUpdates`, `WheelKeepalive`, `AlwaysResendBitmask`.
- `ExtLedDiagMin/Max[6]` (technically diagnostics-panel-state — could be page-state if we make the diagnostics panel a distinct page; keeping plugin-global is fine).
- `StartCaptureOnNextLaunch`, `AutoTestLastSlot`.
- `TelemetryByteLimitOverride`, `TelemetryUploadDashboard`, `TelemetryDownloadDashboard`, `TelemetrySendRateHz`, `TelemetrySendModeFrame`, `TelemetrySendSequenceCounter`.
- `ProfileStore` (SimHub-managed).

### Channel mappings

Move to `Profile.TelemetryChannelMappings : Dictionary<Guid, Dictionary<string, Dictionary<string, string>>>` — keyed page-GUID × dashboard-key × channel-URL. See refactor B.2.

### Delete after migration

- `MozaPluginSettings.PerWheelSlots` (replaced by `Profile.WheelOverridesByPageGuid`).
- `MozaPluginSettings.LoadSlotIntoActive` / `MirrorActiveToSlot` / `GetOrCreateSlot`.
- Flat `volatile int Wheel*` properties on `MozaPluginSettings` (lines 20–109).
- `MozaPluginSettings.TelemetryByWheelUid` (replaced; legacy read for migration).
- `MozaPluginSettings.WheelMzdashFolderByUid` (replaced; legacy read for migration).
- `MozaPluginSettings.TelemetryChannelMappings` (legacy, already in one-shot migration).
- `MozaPluginSettings.TelemetryChannelMappingsByWheel` (replaced; legacy read for migration).
- `MozaPluginSettings.TelemetryProtocolVersion`, `TelemetryFirmwareEraLegacy` (already drain on load).
- `MozaPlugin.ApplySavedWheelSettings`, `ApplySavedDashSettings`, `ApplySavedBaseAmbientSettings`, `ApplySavedHandbrakeSettings`, `ApplySavedPedalSettings`, `ApplySavedAb9Settings` (replaced by per-device `Apply*ToHardware`).
- `MozaPlugin.WriteProfileWheelSettingsToDevice`, `WriteProfileColorsToDevice` (consolidated into wheel apply).
- The detection-branch direct apply calls in `DetectDevices` (replaced by gated per-device hooks).
