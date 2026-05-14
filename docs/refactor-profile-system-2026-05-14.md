# Profile / Settings System Refactor Plan — 2026-05-14

Cites `audit-profile-system-2026-05-14.md` for field-level inventory and gap details.

## Context recap

Four lifecycle events to handle correctly: **cold start**, **game / profile switch**, **wheel swap**, **user UI edit**. Today's mixed keying (wheel-model-name string, MCU-UID hex, game name, per-page-instance SimHub JSON) creates double-source-of-truth bugs and forces detection-flag races. The target shape:

- **Profile** (`MozaProfile`, game-keyed via SimHub `MozaProfileStore`) = single source of truth for hardware-shaping settings.
- **Wheel overlay**, nested in profile, keyed by **wheel page `DescriptorUniqueId` GUID** = wheel-specific delta on top of profile baseline.
- **`MozaPluginSettings`** shrinks to plugin-global toggles + `ProfileStore` + legacy-migration buffers.
- **Device extensions** become a presentation/round-trip layer onto the profile + overlay, not an independent store.

User-stated constraint: no support needed for multiple physically-distinct wheels of the same model. With that simplification, the wheel-page-GUID is enough; MCU-UID-keyed storage becomes a one-off migration source.

## B.1 — Target data model

### `MozaProfile` additions (`UI/MozaProfile.cs`)

```csharp
public class MozaProfile : ProfileBase<MozaProfile, MozaProfileStore> {
    // ... existing motor/FFB/handbrake/pedals/AB9/dash/base-baseline fields ...

    // NEW: per-wheel-model overlay
    public Dictionary<Guid, WheelOverride> WheelOverridesByPageGuid { get; set; }
        = new Dictionary<Guid, WheelOverride>();

    // NEW: channel mappings scoped per profile × wheel page × dashboard
    public Dictionary<Guid, Dictionary<string, Dictionary<string, string>>>
        TelemetryChannelMappings { get; set; }
        = new Dictionary<Guid, Dictionary<string, Dictionary<string, string>>>();

    // NEW: base ambient (was global flat)
    public int BaseAmbientBrightness { get; set; } = -1;
    public int BaseAmbientStandbyMode { get; set; } = -1;
    public int BaseAmbientIndicatorState { get; set; } = -1;
    public int BaseAmbientSleepMode { get; set; } = -1;
    public int BaseAmbientSleepTimeout { get; set; } = -1;
    public int BaseAmbientStartupColor { get; set; } = -1;
    public int BaseAmbientShutdownColor { get; set; } = -1;

    // NEW: gearshift tuning (was global flat)
    public int GearshiftVibrateOnNeutral { get; set; } = -1;  // 0/1 / -1 sentinel
    public int GearshiftDebounceMs { get; set; } = -1;
}
```

### `WheelOverride` (new DTO)

Lives in `UI/MozaProfile.cs` as a nested public class (so it serialises with the profile JSON without extra plumbing). Fields are the union of the wheel-specific subset of `PerWheelSlot` and the wheel-LED block currently duplicated between `MozaProfile` and `MozaPluginSettings`:

```csharp
public sealed class WheelOverride {
    // LED / mode
    public int WheelTelemetryMode { get; set; } = -1;
    public int WheelIdleEffect { get; set; } = -1;
    public int WheelButtonsIdleEffect { get; set; } = -1;
    public int WheelKnobIdleEffect { get; set; } = -1;
    public int WheelKnobLedMode { get; set; } = -1;
    public int WheelButtonsLedMode { get; set; } = -1;
    public int WheelTelemetryIdleSpeedMs { get; set; } = -1;
    public int WheelButtonsIdleSpeedMs { get; set; } = -1;
    public int WheelKnobIdleSpeedMs { get; set; } = -1;
    public int WheelSleepMode { get; set; } = -1;
    public int WheelSleepTimeoutMin { get; set; } = -1;
    public int WheelSleepSpeedMs { get; set; } = -1;
    public int[]? WheelSleepColor { get; set; }                 // packed

    // Brightness (-1 = use profile baseline)
    public int WheelRpmBrightness { get; set; } = -1;
    public int WheelButtonsBrightness { get; set; } = -1;
    public int WheelFlagsBrightness { get; set; } = -1;
    public int WheelESRpmBrightness { get; set; } = -1;

    // ES wheel
    public int WheelRpmIndicatorMode { get; set; } = -1;
    public int WheelRpmDisplayMode { get; set; } = -1;

    // Inputs (newer FW silently drops readback)
    public int WheelPaddlesMode { get; set; } = -1;
    public int WheelClutchPoint { get; set; } = -1;
    public int WheelKnobMode { get; set; } = -1;
    public int WheelStickMode { get; set; } = -1;

    // Colors (packed)
    public int[]? WheelRpmColors { get; set; }
    public int[]? WheelRpmBlinkColors { get; set; }
    public int[]? WheelButtonColors { get; set; }
    public bool[]? WheelButtonDefaultDuringTelemetry { get; set; }
    public int[]? WheelFlagColors { get; set; }
    public int[]? WheelIdleColor { get; set; }
    public int[]? WheelESRpmColors { get; set; }
    public int[]? WheelKnobBackgroundColors { get; set; }
    public int[]? WheelKnobPrimaryColors { get; set; }
    public int[]? WheelKnobRingColors { get; set; }
    public int WheelKnobRingBrightness { get; set; } = -1;

    // Telemetry (per-wheel selection)
    public int TelemetryEnabled { get; set; } = -1;             // -1=unset, 0/1
    public string? TelemetryProfileName { get; set; }           // null=unset
    public string? TelemetryMzdashPath { get; set; }            // null=unset
    public string? TelemetryMzdashFolder { get; set; }          // null=unset
    public int TelemetryWheelEra { get; set; } = -1;            // cast from MozaWheelEra
}
```

Use `Guid` (not `string`) for the dictionary key so JSON serialises cleanly and comparison is canonical. `MozaDeviceConstants.ResolveWheelGuid(modelPrefix)` returns a `string`; convert at the boundary.

### `MozaPluginSettings` after refactor

Keep:

- `ProfileStore`.
- Plugin-global toggles: `ConnectionEnabled`, `LastWheelbasePort`, `LastAb9Port`, `DisableSerialProbeFallback`, `DisableAb9Detection`, `AutoApplyProfileOnLaunch`, `LimitWheelUpdates`, `WheelKeepalive`, `AlwaysResendBitmask`, `ExtLedDiagMin/Max[6]`, `StartCaptureOnNextLaunch`, `AutoTestLastSlot`.
- Telemetry plumbing globals: `TelemetryByteLimitOverride`, `TelemetryUploadDashboard`, `TelemetryDownloadDashboard`, `TelemetrySendRateHz`, `TelemetrySendModeFrame`, `TelemetrySendSequenceCounter`.
- Legacy read-only fields for migration (one release): `TelemetryByWheelUid`, `WheelMzdashFolderByUid`, `TelemetryChannelMappingsByWheel`, `PerWheelSlots`, `TelemetryProtocolVersion`, `TelemetryFirmwareEraLegacy`.

Drop:

- All flat `volatile int Wheel*` properties (lines 20–109).
- `LoadSlotIntoActive`, `MirrorActiveToSlot`, `GetOrCreateSlot`, `_slotsLock`.
- `DashRpmBrightness`, `DashFlagsBrightness`, `DashDisplayBrightness`, `DashDisplayStandbyMin`, `DashRpmBlinkColors`.
- `BaseAmbient*` (move to profile).
- `GearshiftVibrateOnNeutral`, `GearshiftDebounceMs` (move to profile).
- `TelemetryEnabled`, `TelemetryProfileName`, `TelemetryMzdashPath`, `TelemetryMzdashFolder`, `TelemetryWheelEra` (move to wheel overlay; keep a derived read-only accessor that walks the current overlay for back-compat in `ApplyTelemetrySettings`).

### Device-extension DTOs become projections

`MozaWheelExtensionSettings`, `MozaDashExtensionSettings`, `MozaBaseExtensionSettings` keep their DTO shape so existing per-page SimHub JSON deserialises. But:

- `CaptureFromCurrent(MozaPluginSettings, MozaData)` now reads from `ActiveProfile + overlay[pageGuid]` instead of flat fields.
- `ApplyTo(MozaPluginSettings, MozaData)` writes into `ActiveProfile + overlay[pageGuid]` instead of flat fields + slot.
- `WheelModelName` on `MozaWheelExtensionSettings` becomes informational only — the page GUID (resolved from the `DeviceTypeID` the extension was instantiated against) is the actual key. Use `MozaWheelDeviceExtension._expectedModelPrefix` + `MozaDeviceConstants.ResolveWheelGuid` to get the GUID inside `ApplyTo`/`CaptureFromCurrent`. Pass it in as a parameter so the DTO stays static.

## B.2 — Telemetry slot and channel-mapping rekey

### Storage (target)

On `MozaProfile`:

```
TelemetryChannelMappings : Dictionary<Guid,                    // wheel page GUID
    Dictionary<string,                                          //   dashboard key
        Dictionary<string, string>>>                            //     channel URL → property
```

Per-wheel telemetry slot fields (`TelemetryEnabled`, `TelemetryProfileName`, `TelemetryMzdashPath`, `TelemetryMzdashFolder`, `TelemetryWheelEra`) live on `WheelOverride` (see B.1).

### Dashboard key generation

Continue using `MozaPlugin.GetActiveDashboardKeyCandidates()` (`MozaPlugin.cs:1481+`). Format unchanged:

- `wheel:<configJsonId>` — stable across re-uploads of the same dashboard.
- `file:<filename>:<sha1-first-8>` — custom mzdash file.
- `builtin:<name>` — embedded profile.

Same dashboard name across different wheel page GUIDs no longer collides because the GUID layer separates them.

### Migration (one-shot, executed in `MozaPlugin.Init`)

```
foreach uidHex in _settings.TelemetryChannelMappingsByWheel:
    1. Resolve uidHex → wheel model name via _settings.PerWheelSlots scan + WheelModelInfo
       (drop entries with no resolvable model; log).
    2. modelName → pageGuid via MozaDeviceConstants.ResolveWheelGuid(prefix)
       where prefix = WheelModelInfo.ExtractPrefix(modelName).
    3. For each dashboard-key map under uidHex:
       Copy into ActiveProfile.TelemetryChannelMappings[pageGuid][dashKey].
       If no active profile, choose the first profile in store; if none, create default.
    4. Mark migrated entry by leaving original in place until first save under new schema.

foreach uidHex in _settings.TelemetryByWheelUid:
    Same translation. Write into ActiveProfile.WheelOverridesByPageGuid[pageGuid].
    Telemetry{Enabled/ProfileName/MzdashPath} fields move into overlay.

foreach uidHex in _settings.WheelMzdashFolderByUid:
    Same translation. Folder lands in overlay.TelemetryMzdashFolder.
```

Migration runs once and sets a `_settings.SettingsSchemaVersion = 2` marker; on subsequent loads skip if marker is `>= 2`. The legacy dictionaries stay readable (and writable for one release) so a downgrade doesn't lose data.

The empty-UID `""` migration target from the previous one-shot path becomes orphaned-but-preserved: legacy entries land under the empty-UID slot if no model resolution succeeds; surface those in a log entry so the user can decide what to do (rare case).

### Detection binding implication

Channel-mapping reads no longer wait for `wheel-mcu-uid`. `MozaWheelDeviceExtension.Init` resolves the page GUID immediately from `LinkedDevice.DeviceDescriptor.DeviceTypeID`. Bindings load when the extension's page is created in SimHub, regardless of whether the matching physical wheel is connected.

Hardware writes still wait for `wheel-model-name` (the only signal that confirms WHICH wheel page is live). The `_newWheelDetected` flag plus the `_expectedModelPrefix` match in the extension is the gate.

## B.3 — Hardware-apply consolidation

Replace the six existing apply paths (`ApplySavedWheelSettings`, `ApplySavedDashSettings`, `ApplySavedBaseAmbientSettings`, `ApplySavedHandbrakeSettings`, `ApplySavedPedalSettings`, `ApplySavedAb9Settings`) plus the in-`ApplyProfile` write blocks plus the device-extension apply methods with a single entry point per device type:

```csharp
internal void ApplyWheelToHardware(MozaProfile profile, Guid pageGuid) {
    if (!(_newWheelDetected || _oldWheelDetected)) return;
    if (!_data.IsConnected) return;
    profile.WheelOverridesByPageGuid.TryGetValue(pageGuid, out var ov);
    int Eff(int overlay, int baseline) => overlay >= 0 ? overlay : baseline;
    // ... per-field writes guarded by `if (v >= 0)` ...
}
internal void ApplyDashToHardware(MozaProfile profile) { if (!_dashDetected) return; ... }
internal void ApplyBaseToHardware(MozaProfile profile) { if (!_data.IsBaseConnected) return; ... }
internal void ApplyBaseAmbientToHardware(MozaProfile profile) { if (!_baseAmbientLedSupported) return; ... }
internal void ApplyHandbrakeToHardware(MozaProfile profile) { if (!_handbrakeDetected) return; ... }
internal void ApplyPedalsToHardware(MozaProfile profile) { if (!_pedalsDetected) return; ... }
internal void ApplyAb9ToHardware(MozaProfile profile) {
    if (!_ab9Detected || profile.Ab9 == null) return; ...
}
```

Each entry point sources from profile + (for wheel) overlay. Every individual `_deviceManager.WriteSetting` / `WriteColor` is guarded by `if (value >= 0)` (or `!= null` for arrays). No unconditional brightness writes — fixes audit A.2.1.

Invoke from:

- `DetectDevices` branches when each detection flag flips true (replaces the inline `ApplySaved*Settings` calls).
- `OnProfileChanged` — calls all seven against currently-detected hardware.
- UI handlers (immediate apply for the single field, still gated through `IfDetected`).
- `MozaWheelDeviceExtension.SetSettings` / etc. — route through profile/overlay write, then call the matching `Apply*ToHardware` (which is gated, so SimHub's bulk-set-on-startup pass won't push to disconnected hardware).

## B.4 — UI handler hardening

Add to `MozaPlugin` (or a small static helper in `MozaPlugin.cs`):

```csharp
internal void WriteIfWheelDetected(string command, int value) {
    if (_newWheelDetected || _oldWheelDetected) _deviceManager.WriteSetting(command, value);
}
internal void WriteIfDashDetected(string command, int value) { if (_dashDetected) _deviceManager.WriteSetting(command, value); }
internal void WriteIfBaseConnected(string command, int value) { if (_data.IsBaseConnected) _deviceManager.WriteSetting(command, value); }
internal void WriteIfHandbrakeDetected(string command, int value) { if (_handbrakeDetected) _deviceManager.WriteSetting(command, value); }
internal void WriteIfPedalsDetected(string command, int value) { if (_pedalsDetected) _deviceManager.WriteSetting(command, value); }
internal void WriteIfBaseAmbientSupported(string command, int value) { if (_baseAmbientLedSupported) _deviceManager.WriteSetting(command, value); }
// + WriteColor analogues
```

Replace direct `_device.WriteSetting`/`WriteColor` calls in:

- `UI/SettingsControl.xaml.cs:340–1155` — most are `_data.IsBaseConnected`-bound by intent (motor/FFB) but currently unchecked. Wheel-paddles/knob/stick/clutch handlers `:659–719` use `WriteIfWheelDetected`. Handbrake handlers `:760–780` use `WriteIfHandbrakeDetected`. Pedals handlers `:1115–1155` use `WriteIfPedalsDetected`. FFB Eq + Curve `:874–920` use `WriteIfBaseConnected`. AB9 sliders `:2406–2477` use `WriteIfAb9Detected`.
- `Devices/MozaWheelSettingsControl.xaml.cs:404–1046` (LED / mode / color / knob handlers) — all wrap through `WriteIfWheelDetected`.
- `Devices/MozaDashSettingsControl.xaml.cs:163–324` — all wrap through `WriteIfDashDetected`.
- `Devices/MozaBaseSettingsControl.xaml.cs:166–224` — all wrap through `WriteIfBaseAmbientSupported`.

UI handlers ALWAYS update profile/overlay + persist; the hardware write is the conditional part. This means a user can configure a wheel while it's disconnected and the settings stick — they apply when the wheel arrives.

## B.5 — Implementation phasing

Phasing is shaped so each step compiles and ships independently (no big-bang switch). The four-event suite at the end (B.6) verifies each phase.

### R1 — Add target types alongside existing storage

Files: `UI/MozaProfile.cs`, `UI/MozaPluginSettings.cs` (add `SettingsSchemaVersion`), `Devices/MozaDeviceConstants.cs` (no changes; reuse `ResolveWheelGuid`).

Add `WheelOverride` nested class. Add `WheelOverridesByPageGuid`, `TelemetryChannelMappings` (new), base-ambient fields, gearshift fields to `MozaProfile`. Add `CopyProfilePropertiesFrom` coverage for the new fields (`MozaProfile.cs:185–266`).

Add `SettingsSchemaVersion` (`int`, default `0`) to `MozaPluginSettings`.

No behavioural change yet — new fields are populated but not read by any apply path.

### R2 — Migration step in `MozaPlugin.Init`

File: `MozaPlugin.cs:419` area (where `MigrateLegacyChannelMappingsIfNeeded` already lives).

Add `MigrateSettingsToSchemaV2()`. Runs only when `SettingsSchemaVersion < 2`. Walks legacy dictionaries (described in B.2) into the new profile-scoped storage. Sets `SettingsSchemaVersion = 2` and persists.

Defensive: keep all reads going through the legacy dictionaries until R5 lands. R2 is purely additive.

### R3 — `Apply*ToHardware` entry points + detection-flag hooks

File: `MozaPlugin.cs`.

Add the seven `Apply*ToHardware` methods (B.3). Wire them into `DetectDevices` alongside the existing `ApplySaved*Settings` calls (run both during the transition — they should produce identical wire output for a properly-migrated profile). Wire `OnProfileChanged` to call all seven.

Add `WriteIf*` helpers (B.4).

Add the `profile.Ab9 != null` guard inside `ApplyAb9ToHardware` (already present in `ApplyProfile:3539` and `ApplySavedAb9Settings:2459–2463`; preserve in the new entry point).

### R4 — Migrate UI handlers

Files: `UI/SettingsControl.xaml.cs`, `Devices/MozaWheelSettingsControl.xaml.cs`, `Devices/MozaDashSettingsControl.xaml.cs`, `Devices/MozaBaseSettingsControl.xaml.cs`.

Replace direct `_device.WriteSetting`/`WriteColor` calls with `WriteIf*` helpers (B.4). Replace flat-field mutations with profile/overlay mutations — the UI reads back from `_data` (mirrored from profile/overlay by `Apply*ToHardware`) so the visible state stays correct.

Keep `SaveSettings` (`MozaPlugin.cs:923`) as the unified persistence call. After R4 it captures profile state (which is now the source of truth) without needing to mirror flat fields.

### R5 — Migrate device-extension `ApplyTo` / `CaptureFromCurrent`

Files: `Devices/MozaWheelExtensionSettings.cs`, `Devices/MozaDashExtensionSettings.cs`, `Devices/MozaBaseExtensionSettings.cs`, `Devices/MozaWheelDeviceExtension.cs`, `Devices/MozaDashDeviceExtension.cs`, `Devices/MozaBaseDeviceExtension.cs`.

`ApplyTo` writes into `profile + overlay[pageGuid]` (passed in by extension Init/SetSettings; extension knows `_expectedModelPrefix` → page GUID). `CaptureFromCurrent` reads from same.

Update `ApplyWheelExtensionSettings` / `ApplyDashExtensionSettings` / `ApplyBaseExtensionSettings` (`MozaPlugin.cs:3833`/`:3963`/`:4004`) to call `Apply*ToHardware` after `ApplyTo`, instead of running their own hardware-write code paths.

### R6 — Remove legacy stores

After confirming the four-event verification suite passes against schema-v2 storage:

- Delete `MozaPluginSettings.PerWheelSlots`, `LoadSlotIntoActive`, `MirrorActiveToSlot`, `GetOrCreateSlot`, `_slotsLock`, `PerWheelSlot` class.
- Delete flat `volatile int Wheel*` properties.
- Delete `MozaPluginSettings.Dash*`, `BaseAmbient*`, `Gearshift*` flat fields.
- Delete `TelemetryEnabled`, `TelemetryProfileName`, `TelemetryMzdashPath`, `TelemetryMzdashFolder`, `TelemetryWheelEra` flat fields (replaced by overlay).
- Delete `TelemetryByWheelUid`, `WheelMzdashFolderByUid`, `TelemetryChannelMappings`, `TelemetryChannelMappingsByWheel`, `TelemetryProtocolVersion`, `TelemetryFirmwareEraLegacy` after migration cooldown.
- Delete `MozaPlugin.ApplySavedWheelSettings/DashSettings/BaseAmbientSettings/HandbrakeSettings/PedalSettings/Ab9Settings`.
- Delete `MozaPlugin.WriteProfileWheelSettingsToDevice`, `WriteProfileColorsToDevice`.
- Delete the wheel-LED block inside `ApplyProfile` (`:3309–3413`) — `ApplyWheelToHardware` replaces it.

### R7 — Cleanup + verification

- Confirm `MozaWheelExtensionSettings.WheelModelName` is only informational (no key behaviour).
- Confirm `Apply*ToHardware` writes are gated correctly under all four lifecycle events.
- Run the verification suite (B.6).
- Update `todo.md` to mark addressed items struck through.

## B.6 — Verification scenarios

Each scenario gets a wire-trace JSONL capture (the existing `_settings.EnableWireTraceFileSink` toggle drops it to `SimHub/Logs/moza-wire-*.jsonl`) and a manual pass/fail mark.

### Base lifecycle

1. **Fresh install** — Delete `MozaPluginSettings.json` and `*.shmozaprofile`. Connect a wheel cold. Expected: no crashes; brightness/modes follow firmware defaults (no `100/100/15` write storm); no hardware writes before detection completes; profile gets created and gets initial values from `_data` reads.

2. **Profile switch** — Trigger SimHub game switch with a populated MOZA profile (game A → game B). Expected: every profile field with `>= 0` flows to hardware exactly once via `ApplyWheelToHardware` + `ApplyDashToHardware` + ... `ApplyAb9ToHardware`. AB9 block only runs when `profile.Ab9 != null && _ab9Detected`. Wheel overlay layered on top of baseline.

3. **Wheel swap mid-session** — Hot-swap wheel models (GS V2P → CS V2.1). Expected: `ResetWheelDetection` clears flags; re-detection re-resolves page GUID; new model's overlay applies via `ApplyWheelToHardware`; no setting bleed from previous wheel model. The brightness write storm bug from current `ApplySavedWheelSettings` is gone.

4. **UI edit while disconnected** — Disconnect wheel. Change brightness slider in wheel settings control. Expected: overlay updates, `ScheduleSave` debounces (500 ms), no `WriteIfWheelDetected` calls produce hardware writes. Reconnect wheel — verify the freshly-saved brightness applies on re-detection.

5. **Cold start with previously-saved state** — Restart plugin with populated `MozaPluginSettings.json` + profile. Expected: hardware writes happen only after detection flags flip; each value sourced from profile + overlay; no race where flat fields hold stale values.

6. **Empty profile (new game)** — Switch to a game with no MOZA profile. Expected: SimHub creates a new profile (or maps to default); `ApplyProfile`'s zero-detect guard (`MozaPlugin.cs:3270–3281`) still works; AB9 absent (`null`) — `ApplyAb9ToHardware` skips without throwing.

7. **AB9 absent** — `profile.Ab9 == null`. Expected: no `NullReferenceException`; AB9 entry point skips cleanly (guard already exists; verify it still triggers under refactor).

8. **Color-array shape change** — Existing user with packed-int blink colors loads → unpacks correctly. New user → empty arrays default to safe zeros. Verify the "silent truncation on length mismatch" gap (audit A.2.6) fills the tail with default instead of stale residue.

### Channel-mapping coverage

9. **Channel-mapping isolation across wheels** — Profile A on game X, wheel = GS V2P, dashboard "SHDP Default" mapped to channel set α. Switch wheel to CS V2.1 (same profile, same game). The CS V2.1's "SHDP Default" starts empty (or carries its own previously-saved mapping); the GS V2P's α is unaffected.

10. **Channel-mapping isolation across profiles** — Same wheel, two different games (= two different `MozaProfile` instances). Map dashboard "SHDP Default" → channel set α in profile X. Switch game → profile Y. Verify "SHDP Default" in profile Y is independent of α.

11. **Channel-mapping legacy migration** — Start with a `MozaPluginSettings.json` containing the old `TelemetryChannelMappingsByWheel[uid][dashKey][channel]` layout. After load (schema bump from `< 2` to `2`), verify entries land under `Profile.TelemetryChannelMappings[pageGuid][dashKey][channel]` for the resolved page GUID, with no data loss. Verify the legacy dict is not cleared in this release (downgrade-safe) and that `SettingsSchemaVersion` persists.

12. **Channel-mapping legacy with unresolvable UID** — Start with a `TelemetryChannelMappingsByWheel` entry under a UID hex that no current `PerWheelSlots` model name resolves to (orphan). Expected: log warns, entry stays under legacy key, not silently dropped.

### Hot paths

13. **`ApplyTelemetrySettings` after schema-v2** — Verify the path now reads overlay-based telemetry slot instead of flat global. Per-wheel mzdash folder auto-switch still fires from `DetectDevices` wheel-mcu-uid branch (`MozaPlugin.cs:2501–2513`) but reads from `WheelMzdashFolderByUid` legacy until R6, then from overlay.

14. **`MozaWheelDeviceExtension.SetSettings` startup pass** — SimHub calls `SetSettings` on every registered wheel extension at startup. Verify each writes only into its own overlay (keyed by its own page GUID), never into the active hardware path unless the matching wheel is detected. The `modelMatches` gate in the current code becomes "page GUID matches the connected wheel's resolved GUID" check.

Verification artifacts: wire-trace JSONL per scenario in `sim/logs/`. Manual pass/fail checklist appended to this doc at completion.

## Critical files (cited from audit + refactor)

- `UI/MozaPluginSettings.cs` — drop flat fields, slot mechanism, dash/base/gearshift flats.
- `UI/MozaProfile.cs` — add `WheelOverride`, `WheelOverridesByPageGuid`, `TelemetryChannelMappings`, base-ambient, gearshift fields.
- `UI/MozaProfileStore.cs` — unchanged.
- `Devices/MozaWheelExtensionSettings.cs` — `ApplyTo`/`CaptureFromCurrent` reroute to profile+overlay.
- `Devices/MozaDashExtensionSettings.cs` — same; dash is profile-only.
- `Devices/MozaBaseExtensionSettings.cs` — same; base ambient becomes profile-only.
- `Devices/MozaWheelDeviceExtension.cs`, `Devices/MozaDashDeviceExtension.cs`, `Devices/MozaBaseDeviceExtension.cs` — pass page GUID into ext settings methods.
- `Devices/MozaDeviceConstants.cs` — no changes; reuse `GetWheelModelPrefix`, `ResolveWheelGuid`.
- `Devices/DeviceDefinitionDeployer.cs` — no changes; `DescriptorUniqueId` lookup unchanged.
- `MozaPlugin.cs` — major: add `Apply*ToHardware`, `WriteIf*` helpers, migration, remove legacy apply methods, simplify `ApplyProfile`.
- `UI/SettingsControl.xaml.cs`, `Devices/MozaWheelSettingsControl.xaml.cs`, `Devices/MozaDashSettingsControl.xaml.cs`, `Devices/MozaBaseSettingsControl.xaml.cs` — migrate handlers to `WriteIf*` + profile/overlay mutations.

## Existing utilities to reuse

- `Devices/MozaDeviceConstants.ResolveWheelGuid` (`:110–123`), `GetWheelModelPrefix` (`:132–153`) — model-name ↔ page-GUID round-trip.
- `Devices/MozaDeviceConstants.InitializeRegistry` / `SaveRegistryFile` (`:219–265`) — dynamic-model GUID persistence.
- `MozaProfile.UnpackColor` / `PackColor` / `UnpackColorsInto` (`:368–402`) — color round-trip.
- `MozaPlugin.ScheduleSave` (`:969–985`) — 500 ms debounced persistence.
- `MozaPlugin.ResetWheelDetection` (`:2120–2137`) — preserved as-is.
- `MozaPlugin.GetActiveDashboardKeyCandidates` (`:1481+`) — dashboard-key resolver.
- `MozaPlugin.ApplyTelemetrySettings` (`:1151+`) — refactor internals to read overlay, keep surface.

## Out of scope (note for follow-ups)

- Per-physical-wheel telemetry binding (two GS V2Ps with different mzdash) — user confirmed not needed; MCU-UID-keyed storage retired.
- Dashboard upload scaffolding (`BuildStagingBlock`, `BuildTransferManifest` in `Telemetry/DashboardDownloader.cs`) — preserved per memory `feedback_preserve_upload_scaffolding`.
- `WheelKeepalive` / `AlwaysResendBitmask` could plausibly migrate to per-wheel-overlay (they're firmware workarounds), but they're plugin-debug toggles and stay global for now.
