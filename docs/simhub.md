# SimHub Plugin API Reference

Notes on SimHub's plugin API gathered from decompiling `SimHub.Plugins.dll` and building the MOZA plugin. SimHub does not publish official plugin docs, so this serves as a working reference.

## Plugin Interfaces

A plugin class implements one or more interfaces and is decorated with metadata attributes:

```csharp
[PluginDescription("...")]
[PluginAuthor("...")]
[PluginName("...")]
public class MyPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
```

### IPlugin

Core lifecycle — every plugin implements this.

| Member | Description |
|--------|-------------|
| `PluginManager PluginManager { set; }` | Injected by SimHub before `Init` |
| `string LeftMenuTitle { get; }` | Label shown in SimHub's left nav |
| `ImageSource PictureIcon { get; }` | Icon for the nav (nullable) |
| `void Init(PluginManager pluginManager)` | Called once at startup |
| `void End(PluginManager pluginManager)` | Called on shutdown |

### IDataPlugin

Adds a per-frame callback driven by the game loop.

| Member | Description |
|--------|-------------|
| `void DataUpdate(PluginManager pluginManager, ref GameData data)` | Called every frame. `data.GameRunning`, `data.NewData.Rpms`, `data.NewData.MaxRpm`, flags, etc. |

### IWPFSettingsV2

Provides a settings UI shown in SimHub's plugin pane.

| Member | Description |
|--------|-------------|
| `Control GetWPFSettingsControl(PluginManager pluginManager)` | Return a WPF `UserControl` |

## Settings Persistence

SimHub provides JSON-based settings persistence via extension methods on `IPlugin`:

```csharp
// Read (deserializes from SimHub's settings directory, or creates default)
_settings = this.ReadCommonSettings<MySettings>("key", () => new MySettings());

// Write
this.SaveCommonSettings("key", _settings);
```

The settings object can be any serializable class. Newtonsoft.Json is used for serialization.

## Properties and Actions

Plugins can expose named properties (readable from dashboards/other plugins) and actions (triggerable from input mappings):

```csharp
// Properties — lambda evaluated each frame
this.AttachDelegate("MyPlugin.SomeValue", () => _data.SomeValue);

// Actions — triggered by user-bound buttons/keys
this.AddAction("MyPlugin.DoSomething", (a, b) => { ... });
```

## Logging

```csharp
SimHub.Logging.Current.Info("message");
SimHub.Logging.Current.Error("message");
```

Writes to SimHub's log file.

## Profile System (`SimHub.Plugins.ProfilesCommon`)

SimHub has a built-in per-game profile system. Plugins provide a profile data class and a store; SimHub handles switching profiles when the active game changes.

### Core Types

**`ProfileBase<TProfile, TSettings>`** — Base class for a profile. Subclass and add your settings properties.

| Member | Description |
|--------|-------------|
| `string Name { get; set; }` | Profile display name |
| `string DisplayName { get; }` | Formatted name (includes game info) |
| `Control ProfileContentControl { get; }` | Optional WPF control for editing profile fields. Return `null` if not needed. |
| `void CopyProfilePropertiesFrom(TProfile p)` | Deep-copy all settings from another profile (used by clone) |

**`ProfileSettingsBase<TProfile, TSettings>`** — Base class for the profile store. Manages the collection of profiles and current selection.

| Member | Description |
|--------|-------------|
| `List<TProfile> Profiles` | All profiles |
| `TProfile CurrentProfile { get; set; }` | Active profile |
| `ObservableCollection<TProfile> SortedProfiles` | Sorted/observable, used by UI bindings |
| `ProfileSwitchingMode ProfileSwitchingMode` | How profiles switch on game change |
| `string FileFilter` | File dialog filter for import/export (e.g. `"My profile (*.myprofile)\|*.myprofile"`) |
| `void Init()` | Call during plugin init. Reads `PluginManager.Instance.GameName` and selects the matching profile. |
| `void AddProfile(TProfile p)` | Add a new profile |
| `event EventHandler CurrentProfileChanged` | Fires when the active profile changes (game switch or manual) |
| `void InitProfile(TProfile p)` | Override to run setup on deserialized profiles |

**`ProfileSwitchingMode`** — Enum controlling auto-switch behavior:
- `Disabled` — Manual only
- `LastUsedPerGame` — Remember last profile per game
- `BestMatch` — SimHub picks the closest match

**`IProfileSettings` / `IProfileSettings<TProfile>`** — Interfaces implemented by `ProfileSettingsBase`. Required by the UI controls.

### Wiring Up Profiles

```csharp
// In Init():
var store = _settings.ProfileStore;
if (store.Profiles.Count == 0)
    store.Profiles.Add(new MyProfile { Name = "Default" });
store.Init();  // reads current game, selects profile
store.CurrentProfileChanged += OnProfileChanged;

// Apply initial profile
if (store.CurrentProfile != null)
    ApplyProfile(store.CurrentProfile);
```

The store is typically a property on your settings class so it's persisted alongside other settings via `SaveCommonSettings`.

### Profile UI Controls

SimHub provides ready-made WPF controls for profile management. These live in `SimHub.Plugins.ProfilesCommon` (assembly `SimHub.Plugins`).

**`ProfileCombobox`** — Styled dropdown showing all profiles with game icons.

```xml
xmlns:profilescommon="clr-namespace:SimHub.Plugins.ProfilesCommon;assembly=SimHub.Plugins"

<profilescommon:ProfileCombobox ProfileSettings="{Binding MyProfileStore}" />
```

| Property | Type | Description |
|----------|------|-------------|
| `ProfileSettings` | `IProfileSettings` (DependencyProperty) | The profile store to bind to |

Internally renders a MahApps `MetroComboBox` bound to `ProfileSettings.SortedProfiles` with `SelectedItem` bound to `ProfileSettings.CurrentProfile`.

**`ProfileList`** — Complete profile management bar: dropdown + Profiles manager / Edit / Clone / New buttons.

```xml
<profilescommon:ProfileList DataContext="{Binding MyProfileStore}" />
```

| Property | Type | Description |
|----------|------|-------------|
| `AdditionalActionButtons` | `object` | Slot for extra buttons (content property) |
| `RightContent` | `object` | Slot for content on the right side |

The `ProfileList` internally creates a `ProfileCombobox` and wires it to the `DataContext`. It also creates a `ProfileHandler` that provides click handlers for New/Clone/Edit/Manage.

**`ProfilesManager<TProfile, TSettings>`** — Modal dialog for full profile management (import/export, drag-drop, reorder, profile switching mode).

```csharp
var manager = new ProfilesManager<MyProfile, MyStore>(store);
manager.ShowDialogWindow(parentControl);
```

Inherits from `SimHub.Plugins.UI.SHDialogContentBase`.

**`ProfileHandler<TProfile, TSettings>`** — Used internally by `ProfileList`. Provides `LoadProfile_Click`, `CloneProfile_Click`, `EditProfile_Click`, `NewProfile_Click` handlers.

## UI Utilities

**`SimHub.Plugins.UI.SHDialogContentBase`** — Base class for modal dialogs. Call `.ShowDialogWindow(parent)` to display.

## GameData Reference

Available in `DataUpdate` via `data.NewData` (type `GameReaderCommon.StatusDataBase`):

| Property | Type | Description |
|----------|------|-------------|
| `Rpms` | `double` | Current engine RPM |
| `MaxRpm` | `double` | Max engine RPM |
| `Flag_Checkered` | `int` | Nonzero when flag active |
| `Flag_Black` | `int` | |
| `Flag_Orange` | `int` | |
| `Flag_Yellow` | `int` | |
| `Flag_Blue` | `int` | |
| `Flag_White` | `int` | |
| `Flag_Green` | `int` | |
Check `data.GameRunning` and `data.NewData != null` before accessing.

## PluginManager Properties

Plugins can read SimHub-wide properties via `pluginManager.GetPropertyValue("name")`. These are available at startup (no game required).

| Property | Type | Description |
|----------|------|-------------|
| `DataCorePlugin.GameData.TemperatureUnit` | `string` | Global temperature unit preference (`"Celsius"` or `"Fahrenheit"`), configured at first launch |

## MahApps Metro

SimHub's UI is built on [MahApps.Metro](https://mahapps.com/). Plugin UIs can use MahApps controls (`MetroComboBox`, `ToggleSwitch`, etc.) for consistent styling. The assemblies are already loaded by SimHub at runtime.
