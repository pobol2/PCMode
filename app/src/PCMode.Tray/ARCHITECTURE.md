# PCMode.Tray Architecture

`PCMode.Tray` is PCMode's WPF presentation layer. It uses explicit
composition, page-family controls, and feature folders so shared UI stays
consistent without hiding construction or behavior behind reflection or a
service locator.

This project was built under the working name `PCMode.Tray.Target`,
alongside an earlier WPF tray implementation, and took over the
`PCMode.Tray` name once that earlier implementation was retired. Its
namespaces were `PCMode.Tray.*` from the start (see the `.csproj`'s
`RootNamespace`) specifically so that handover needed no mechanical rename of
any file. A few passages below still note where current behavior was a
deliberate departure from that earlier implementation's design.

## Dependency direction

```text
App / Shell / Navigation
          |
          v
       Features
          |
          v
Controls / Presentation / Configuration
          |
          v
      PCMode.Engine
```

`PCMode.Tray` may depend on `PCMode.Engine`; the engine must not depend on WPF
or the tray project. Engine behavior and persistence stay in the engine. The
tray owns presentation state, dialogs, and user interaction.

## Composition and navigation

`App` (`App.xaml.cs`) is the composition root and the only place that `new`s
up the pieces below. Nothing is resolved through a container or reflection --
every dependency a class needs is a constructor parameter, so the whole
object graph is readable from `App.Start()` top to bottom.

### Startup sequence

`OnStartup` wires three process-wide unhandled-exception handlers (dispatcher,
`AppDomain`, unobserved task) to `LocalLog` before anything else runs, then
calls `Start(e)` inside a try/catch that shows a message box and exits with
code 1 on any startup failure. `Start` itself, in order:

1. Parses CLI args (`TrayOptions.Parse`) and exits on a bad command line.
2. Acquires `Composition/Runtime/SingleInstance` *before* anything else claims a
   resource -- a second launch must not create a second tray icon. If this
   process is not the owner, it asks the existing owner to activate its
   window (unless launched with `--background`) and exits immediately.
3. Resolves the data root and checks whether `config.json` exists yet
   (`firstRun`) -- used later only to force the main window visible before
   the setup wizard opens over it; whether the wizard is *offered* is a
   separate, persisted decision (`ApplicationSettings.SetupCompleted`).
4. Builds `LocalLog`, then the real engine (`EngineBootstrap.Create`, given
   the one process-lifetime `PartyAudioHost`) and `ApplicationSettingsStore`,
   inside a second try/catch that treats an unreadable configuration as a
   startup failure rather than a page-level error.
5. Resolves command-line overrides (`TrayOptions.Resolve`) over the persisted
   `ApplicationSettings`, failing startup if an override cannot be applied
   (e.g. an unparseable `--favorite`).
6. Wraps the engine in `EngineRuntime`.
7. Builds `Platform/Themes/ApplicationThemeService`, applies the resolved theme, and
   registers a class handler on `Window.Loaded` so every window this process
   ever creates -- including ones built later, like the setup wizard -- gets
   its title bar themed automatically the first time it is shown.
8. Builds the one `IConfigurationWorkspace` for the process, via
   `ConfigurationWorkspace.For(root, log, party, activate: ActivateRuntime)`
   -- `ActivateRuntime` is what every page's save eventually reaches (below).
9. Builds `PageCatalog` (passed `party`/`runtime`/`log`/`settingsStore`, the
   `openSetupWizard` callback, and an `onSettingsSaved` callback -- below),
   calls `Validate(NavigationRegistry.Items)` to fail fast on a malformed
   navigation tree, then `ShellPages` and `MainWindow`, and themes the window.
10. Builds `ExitCoordinator`, then starts the picker and its input surfaces
    (`StartPicker`, below) unless launched `--no-input`.
11. Builds the tray `NotifyIcon` and context menu (`Composition/Shell/PCModeIcon`,
    `Composition/Shell/TrayMenu`, themed via `Platform/Themes/TrayMenuTheme`), and shows the
    window unless launched `--background` (first run always shows it
    regardless).
12. Repairs the Windows startup registry entry to match persisted intent
    (`SynchronizeAutoStart`, logged, never blocking) and, if
    `SetupCompleted` is still false, dispatches showing the setup wizard.

### Runtime hosting

- an `EngineRuntime` (from `PCMode.Engine`), built once at startup and held
  for the process's life; `EngineRuntime.Engine` is a live `Volatile.Read`,
  so a page that needs the *current* engine (Diagnostics, Television's live
  power lookup) reads it fresh at the point of use rather than caching the
  reference, since `Reload()` replaces it;
- `Composition/Runtime/SingleInstance` -- refuses a second process and activates the
  existing window instead, via its own named mutex
  (`Local\PCMode.Shell.SingleInstance`);
- `Composition/Runtime/ExitCoordinator` -- lets a running transition finish instead of
  killing it when the user exits, asked in terms of `ModeEngine.IsBusy`
  directly (there is no shell-wide status view model to ask instead --
  see "Permanently excluded" below); `App.AskAboutExit` supplies the
  three-way question (wait then exit / exit and leave it running / stay),
  `App.ShutdownForReal` is the only path that sets `MainWindow.AllowClose =
  true` before calling `Shutdown`;
- `Platform/Themes/ApplicationThemeService` -- owns the one semantic color palette
  every WPF surface reads (`Framework/Layout/Styles.xaml`'s 13 keys), resolves
  System/Light/Dark against the Windows personalization registry key, swaps
  the resource dictionary's live values, and sets each window's native
  title bar to match via `DwmSetWindowAttribute`. Raises `ThemeChanged`,
  which `App` uses to re-theme the tray's `ContextMenuStrip`
  (`Platform/Themes/TrayMenuTheme`, a WinForms surface the WPF resource system
  cannot reach directly);
- the picker and its input surfaces (`Platform/Picker/PickerHost`, `Platform/Input/GlobalHotkeys`,
  `Platform/Input/ControllerPoller`) -- started by `App.StartPicker` unless launched
  `--no-input`, and restarted (not just reconfigured) whenever Settings or
  Spaces' quick-access section saves a change that affects them
  (`App.OnSettingsSaved`: re-applies the theme, tears down and rebuilds the
  picker with the new favorites/shortcuts/monitor/controller setting, then
  rebuilds the tray menu). A refusal to start (a hotkey another program
  already owns) is logged, not a startup failure -- see `Platform/Picker/PickerHost`;
- a `NotifyIcon` and `Composition/Shell/TrayMenu` -- one menu item per configured target,
  built straight from `ModeEngine.Targets`/`ReadCurrentTargetKey`, plus Show
  Picker (when the picker started), Open and Exit. `Composition/Shell/MainWindow` hides
  on close instead of exiting (cancels its own `Closing` unless `AllowClose`
  is set); the tray icon is what keeps the process alive. Left-clicking the
  icon or choosing a target rebuilds the menu afterward (`RebuildTrayMenu`)
  so its checked target and idle state stay current. `TrayMenu.StatusText`
  only ever reports the current target's name (`"PCMode — {label}"`), since
  there is no shell-wide status model to ask for richer wording -- see
  "Permanently excluded" below.

`IConfigurationWorkspace.Apply(string saved)` is what a page's save reaches
to make the write actually take effect: `App.ActivateRuntime` calls
`EngineRuntime.Reload()` and, only on success, rebuilds the tray menu --
`Apply` itself never touches WinForms tray state directly, since
`EngineRuntime.Reload` alone is safe to call off the UI thread but rebuilding
the tray icon is not. `ConfigurationWorkspace.For(...)` takes the callback as
an optional `activate` delegate; null (the case in tests) means the write
still lands on disk, it just does not take effect until something else
reloads.

- `NavigationRegistry` defines page labels and parent relationships as a flat
  list of `NavigationItem(PageId, Label, Parent)` records -- no page owns its
  own place in the tree; `PageCatalog.Validate` checks the whole list for
  duplicates, missing parents, cycles, and pages with no registered factory
  before `MainWindow` ever renders it.
- `PageCatalog` constructs feature pages and their ViewModels. Its
  constructor takes the one `IConfigurationWorkspace` every page needs, plus
  a growing list of *optional* capabilities only some pages need
  (`PartyAudioHost? party`, `EngineRuntime? runtime`, `LocalLog? log`,
  `ApplicationSettingsStore? settingsStore`, `Action? openSetupWizard`,
  `Action<ApplicationSettings>? onSettingsSaved`) -- each nullable so a bare
  `PageCatalog` built in a test still constructs every page, just without
  whatever that capability would have wired up. `Create(PageId)` looks the
  page up in a `Dictionary<PageId, Func<FrameworkElement>>` built once in the
  constructor and throws if nothing is registered for it.
- `ShellPages` creates pages lazily via `PageCatalog.Create`, caches one
  instance per `PageId` in a dictionary, and is the only thing that calls
  `IPageLifecycle.OnActivated`/`OnDeactivated` for ordinary navigation:
  `Activate(page)` gets (or creates) the target page, deactivates whatever
  was previously active, then activates the new one -- a no-op if the
  requested page is already active. A page embedded outside this path (the
  setup wizard, below) does not get this for free.
- `MainWindow` renders navigation and displays the selected cached page by
  calling `ShellPages.Activate`.

Navigation metadata and feature ownership are separate. For example,
Applications is shown under Devices but remains in `Features/Applications`.

### First-run setup wizard

`Composition/Setup/SetupWizardWindow` is a `Window`, not a page in `NavigationRegistry`
-- it is opened directly by `App`, not navigated to. It embeds three real,
already-constructed feature pages as its Welcome/Devices/Spaces/Settings/
Finish steps (`PageCatalog.Create(PageId.Devices)` etc., fresh instances,
deliberately *not* `ShellPages`-cached, so the wizard's copies are
independent of whatever the main window has open) rather than duplicating
their UI. Because these pages are not owned by `ShellPages`, the wizard
calls `IPageLifecycle.OnActivated`/`OnDeactivated` on each step's page
itself as the step changes -- mirroring exactly what `ShellPages.Activate`
does for ordinary navigation, just driven by the wizard instead. It saves
nothing of its own except the one `application.setup.completed` flag on
Finish or Skip (`ApplicationSettingsStore.MarkSetupCompleted`); every other
save happens through the embedded pages' own normal save paths.

Reopening the wizard resumes rather than restarts, using a three-way
heuristic: straight to Devices while no display layout is
configured yet, then Spaces while no target is configured yet, otherwise
straight to the Finish summary (`App.ShowSetupWizard`, reading the layout
count off `EngineRuntime.Engine.Providers.OfType<DisplayProvider>()` and the
target count off `EngineRuntime.Engine.TargetKeys`). The automatic first-run
offer bypasses this and always starts at Welcome.

## Permanently excluded

This project's predecessor -- an earlier WPF tray implementation, since
retired -- had some shell-wide machinery that was deliberately not carried
forward. Naming it here, once, means a page or a composition-root comment
can point at this section instead of re-explaining the decision inline:

- **A shell-wide status view model and single polling timer.** The
  predecessor ticked every built page's status on one timer regardless of
  whether it was visible. This project has no equivalent. Every page or
  surface that needs live engine state (`Home`, `Diagnostics`,
  `Composition/Shell/TrayMenu`, `Platform/Picker/PickerHost`, Television's
  power lookup, Layouts' monitor state) reads `EngineRuntime`/`ModeEngine`
  directly at the point of use instead, either as a one-shot read on
  activation or through the page-owned
  `ConfigurationPageShell.EnableLiveRefresh` timer (see "Configuration
  pages" below). The tray's own status surfaces are smaller as a direct
  consequence: `Composition/Shell/TrayMenu.StatusText` reports only the
  current target's name, not a richer Running/Failed/Uncertain wording, and
  `Composition/Runtime/ExitCoordinator` asks `ModeEngine.IsBusy` directly
  rather than a richer idle/activity model.
- **A `--script` / `--own-input` compatibility shim.** The predecessor
  recognized these two long-removed switches by name and explained what
  replaced them, because a real, previously-working command line for it
  could still pass them. This project never shipped either switch, so there
  is no prior command line for it to explain the removal of; an unrecognized
  argument falls through to the ordinary "not an option PCMode understands"
  message instead (see `Composition/Runtime/TrayOptions`, and its own test
  class remarks).

A capability being read directly instead of through a shared model is the
expected shape here, not a gap to close -- see "Configuration pages" for the
two sanctioned ways a page keeps itself current while open.

## Page hierarchy

Every full feature page derives from a shared page family:

```text
CommonPageShell : ContentControl
|-- ListPageShell
|   |-- HomePage
|   |-- DevicesPage, DiagnosticsPage (implement IPageLifecycle directly)
|   `-- SettingsPage (implements IPageLifecycle directly; see below)
`-- ConfigurationPageShell : IPageLifecycle
    |-- ApplicationsPage, AudioPage, CursorPage, RgbPage, ThemePage
    `-- LayoutsPage, TelevisionPage (also opt into EnableLiveRefresh)
```

`CommonPageShell` owns the universal header (`PageTitle`/`PageDescription`),
`HeaderActionContent`, `FooterContent`, margins, and spacing -- a plain
`ContentControl` with a stable default style key so implicit styling matches
every derived page. `ListPageShell` adds the standard scrolling content
layout on top and nothing else; it does not implement `IPageLifecycle`
itself, so a `ListPageShell` page that needs to do something on every
activation (Devices' hardware rescan, Diagnostics' refresh, Settings'
reload) implements the interface directly rather than inheriting it.

**Settings is the one editable page that is not a `ConfigurationPageShell`
page.** It has a real Save/Discard footer, but no selector, no named or
creatable entries, and nothing to duplicate or delete -- the parts of
`ConfigurationPageShell` that exist to manage a *collection* of saved items
do not apply to a single always-present settings form. `SettingsPage`
derives from `ListPageShell` and wires its own Save/Discard buttons directly
to `SettingsViewModel.Save()`/`Refresh()`; `SettingsViewModel` derives from
plain `ObservableObject`, not `ConfigurationPageViewModelBase`, and has no
dirty tracking (Discard always just reloads from disk; Save is always
clickable while a store is available). This mirrors this project's
predecessor's own `SettingsPage` shape and is treated as an accepted
exception rather than a gap: per "Reuse established page families," a new
shared shape is justified only once a second real page needs it, and no
other page here has Settings' single-form-with-no-collection shape. `ThemePage`
shows the shape that *does* still fit `ConfigurationPageShell` -- it reuses
the shell purely for its selector/editor frame and `PrimaryAction` button
(Light/Dark as two selector entries), but declares no Save/Discard footer
and its `ViewModel.SaveChangesCore()` is a no-op, since choosing a theme is
never persisted to configuration.

`ConfigurationPageShell` implements `IPageLifecycle` and owns the
selector/editor frame, shared actions, Save/Discard controls, selection
protection, and first-activation loading:

- **First load vs. every load.** `OnActivated()` calls
  `viewModel.Refresh()` only the *first* time a page is activated; every
  activation after that calls the virtual `OnReactivated(viewModel)`
  instead (base implementation does nothing). This split is what makes
  "page refresh does not discard unsaved edits" true structurally rather
  than by convention -- a feature overrides `OnReactivated` for a one-shot
  ambient refresh that does not touch the loaded editor state (Applications
  refreshes running-state badges this way). A page needing *continuous*
  refresh while it stays open, not just once per visit (Layouts' live
  monitor state, Television's live power state), instead calls
  `EnableLiveRefresh` -- see "Configuration pages" below.
- **Selector-selection sync and Save/Discard buttons are generic at the
  shell level.** `DataContextChanged` subscribes to the ViewModel's
  `PropertyChanged` and keeps `SelectorSelectedItem` in sync with
  `SelectedKey` in both directions (`OnSelectorSelectionChanged` calls
  `viewModel.SelectKey(nextKey)` directly; a `SelectedKey` change from the
  ViewModel side re-syncs the visible selection) -- a feature page never
  needs its own selection-sync bookkeeping or a `_syncingSelection` flag.
  `OnSaveClick`/`OnDiscardClick` call `viewModel.TrySaveChanges()`/
  `DiscardChanges()` directly for the same reason.
- **`ConfirmDiscardIfDirty()`, `PromptAndApply(...)`, and
  `ConfirmAndApply(...)`** are protected helpers on the base: every
  Create/Rename/Duplicate/Delete dialog and every selector switch already
  guards against silently discarding an unsaved edit (a Yes/No/Cancel
  prompt that can save, discard, or cancel the switch), so a feature page
  only supplies wording and what a name means to it, not the guard itself.
  A page action that bypasses those two helpers (an enable/disable toggle,
  a "make default" button) calls `ConfirmDiscardIfDirty()` itself first.
  Because the guard now lives only at the shell level, a `ConfigurationPageShell`
  ViewModel's own public mutating methods (create/rename/duplicate/delete,
  `SelectedKey`'s setter) are not themselves safe to call while dirty from
  outside the shell's click handlers -- consistent across every feature, and
  the trade-off the predecessor's own per-method dirty re-checks existed to
  avoid.

Feature pages supply only feature-specific content, bindings, templates,
dialogs, and view interactions. Repeated structure belongs in the nearest
shared shell or component.

## Configuration pages

`ConfigurationPageViewModelBase` owns common editor state:

- `IsBusy`, `HasUnsavedChanges`/`CanDiscard`, `StatusMessage`, `ErrorMessage`;
- `SelectedKey` (protected setter) -- what the selector should show as
  selected; a concrete ViewModel typically wraps this in a typed property
  (`SelectedTelevision`, `SelectedLayout`) and implements the abstract
  `SelectKey(object)`/`Refresh()`/`CanSave` members;
- `SetDirtyProperty<T>(ref field, value)` -- the property-setter helper
  every editable field uses, which also calls `MarkDirty()` when the value
  actually changed; `MarkDirty()`/`ClearDirtyState()` for cases a plain
  property setter doesn't cover (a row inside a nested collection changing,
  a fresh draft being built);
- `RunWithoutDirtyTracking(Action)` -- suppresses `MarkDirty()`/
  `SetDirtyProperty` for the duration of the action, used while populating
  the editor from a freshly loaded item so loading it doesn't itself mark
  the page dirty;
- `TrySaveChanges()`/`DiscardChanges()` -- the public entry points the shell
  calls, backed by the abstract `SaveChangesCore()`/`DiscardChangesCore()`
  every feature implements;
- `NotifyValidationChanged(params string[])` -- raises property-changed for
  whatever a feature's `ValidationProblem`/`CanSave` override depends on;
- the shared `Workspace` property and its `ReadProviders<T>()`/
  `ReadProvider<T>()`/`ReadConfiguration()` helpers, which build providers
  or the configuration document from the data root with one shared
  `ModeConfigurationException` handling path, and the static `SafeRead()`
  helper for a provider's own best-effort read.

`ValidationProblem` (virtual, defaults to null) is shown by the shared
footer template automatically -- a feature overrides it instead of wiring
its own `FooterStatusContent` binding. Not every page has an "all items"
provider to read through `ReadProviders<T>()`: the engine only ever builds
resources for whichever named entries a mode-configuration document
actually points at (Television's catalog, for instance, only projects its
one *default* television onto the historical resource keys), so a page
whose full named collection isn't otherwise exposed reads its own
configuration-store type directly (`LgTvConfigurationStore.Read(Workspace.Root)`,
matching the pattern `LayoutConfigurationStore`/`CursorPositionConfigurationStore`
already use for their own full collections).

Concrete ViewModels own fields, validation, engine mappings, and feature
operations. `IConfigurationSelectorItem` (`Framework/Presentation/`) is the minimal
contract a selector-bound item implements -- just `object Key { get; }` --
so `ConfigurationPageShell` can match a ViewModel's `SelectedKey` against
whatever collection `SelectorItemsSource` is bound to without knowing the
feature's concrete item type. `Framework/Presentation/ObservableEditableRow` is the
shared base for a plain "row with an injected onChanged callback"
(Applications' requirement rows, Television's input/setting/profile rows,
Rgb's LED/device/scene rows) -- a row derives from it instead of
reimplementing the same SetProperty-then-notify-the-owner shape. Layouts'
own monitor row does not derive from it (documented in `LayoutsViewModel`'s
remarks as an intentional non-generalization: its dirty tracking needs to
distinguish a real edit from a drag preview, which the generic row shape
does not have a hook for).

`IConfigurationWorkspace` is the presentation-to-engine boundary:

- `Root` -- the data root, or null for the engine default;
- `Read()` / `ReadProviders()` -- the configuration document and/or the
  providers it configures, built fresh from disk on every call, the same
  production pipeline a real startup uses;
- `Apply(string saved)` -- makes a saved change on disk the running
  configuration (see Runtime hosting above), and returns what to tell the
  user: `saved` unchanged when there is nothing running to activate or
  activation succeeded, with the reason appended otherwise;
- `Note()` / `Warn()` / `Fault()` -- diagnostics logging, backed by a small
  file-based `LocalLog` the composition root constructs once and every page
  shares.

Feature configuration stores remain responsible for writes. Every page's save
path calls `Workspace.Apply(message)` at the point it used to call
`Workspace.Note(Category, message)` -- the one place per feature where a
change actually reached disk -- so a save takes effect immediately instead of
only after the next restart. Live, non-persisting operations (activating an
application, applying a target, toggling a light scene, checking a
television's power) still report through `Note`: there is nothing on disk
for a reload to pick up.

A capability every configuration page needs (diagnostics logging above) grows
the shared interface. A capability only one or two features need stays a
constructor argument instead: `AudioViewModel` and `SpacesViewModel` both take
an optional `PartyAudioHost`, the one object the composition root hands to
every `AudioProvider` and every ad-hoc `ModeEngine` it builds so a Party
mirror started through one page is the mirror every other page's `Discover()`
still sees; `TelevisionViewModel` takes an optional
`Func<string?, TvPowerProvider?>` the same way, resolved from
`EngineRuntime.Engine.Providers` so a live pairing/status check shares the
one connection a real transition would use rather than opening a second
socket to the same device. Growing `IConfigurationWorkspace` for either
would have made every other page's constructor carry a capability it does
not use.

Configuration pages are cached. Their first activation loads configuration;
later activations may refresh ambient state without overwriting unsaved
edits, via one of two patterns:

- **`OnReactivated(viewModel)`** -- a one-shot refresh each time the page
  becomes active again (Applications' running-state badges). Cheapest, and
  the default choice.
- **`ConfigurationPageShell.EnableLiveRefresh(TimeSpan interval, Func<Task> tick)`**
  -- a shared `LiveRefreshTimer` (`Framework/Controls/PageShells/LiveRefreshTimer.cs`)
  a page opts into once, from its own constructor, instead of owning a timer
  directly. `ConfigurationPageShell` starts it in `OnActivated()` and stops it
  in `OnDeactivated()`, so it only ever ticks while that page is the one on
  screen. `ListPageShell` exposes the identical `EnableLiveRefresh` method for
  pages with no unsaved-edit state to protect. `tick` must only update
  observed state -- never configuration, never the editor's unsaved fields.
  Callers today: Layouts' and Television's live monitor/power state,
  Applications' running-state badges (all `ConfigurationPageShell`), and
  Home's/Devices'/Diagnostics' own periodic refresh (`ListPageShell`).
  Settings, and configuration pages with nothing ambient to observe (Spaces,
  Cursor, Rgb, Theme, Audio), deliberately do not call it -- there is nothing
  to refresh there except the editor's own unsaved fields. Centralizing the
  timer in the shell (rather than each page owning a raw `DispatcherTimer` and
  overriding `OnActivated`/`OnDeactivated` itself) means a page cannot forget
  to stop its timer on deactivation, since the shell always does.

## Shared UI ownership

Reusable controls live under `Framework/Controls/Components`:

- `PageSection` for titled sections;
- `FieldRow` for label/value layout;
- `BrowseFileRow` for executable paths;
- `RemovableRow` for editable collection rows;
- `ConfigurationSelectorItem` for selector entries (`Title`, `BadgeText`,
  `IsBadgeVisible` -- a feature's selector item template sets these, it does
  not need its own badge layout);
- `StatusDot` for state indicators.

Dialogs used by more than one feature live under `Framework/Dialogs/` --
`AliasPromptWindow` (the name-entry dialog behind `PromptAndApply`) is the
one every Create/Rename/Duplicate flow shares.

Resources have one owner:

- `Framework/Layout/Styles.xaml`: application-wide colors, typography, controls, and
  converters. Binds the 13-key semantic brush palette (`AccentBrush`,
  `WindowBackgroundBrush`, `SurfaceBrush`, `ForegroundBrush`, etc.) via
  `DynamicResource`, but does not define the unprefixed keys itself;
- `Platform/Themes/Dark.xaml` / `Platform/Themes/Light.xaml`: the two palettes, each
  defining the same 13 keys under a `Dark.` or `Light.` prefix.
  `ApplicationThemeService` captures both palettes by key prefix at startup
  and writes the unprefixed, in-`Resources` copy of each key from whichever
  one currently applies -- the merge order of the two files in `App.xaml` is
  not what decides which is visible at any moment, `ApplicationThemeService.Apply` is;
- `Framework/Layout/PageShellLayout.xaml`: common and list page templates;
- `Framework/Layout/ConfigurationPageLayout.xaml`: configuration-page template and
  dimensions;
- `Framework/Layout/ComponentLayout.xaml`: reusable component templates.

The 13-key palette does not yet distinguish every WinForms tray-menu visual
state: `Platform/Themes/TrayMenuTheme` has no separate pressed/selected
brush, so the tray's `ContextMenuStrip` hover, pressed, and selected states
all render with the same `SurfaceHoverBrush`. This is a known, unresolved
gap, not an oversight -- widening the palette to give each state its own
color is future work, out of scope here.

Do not duplicate shared resource keys or feature-local copies of these
structures.

## Code organization

Every folder below sits at `app/src/PCMode.Tray/<folder>` unless
otherwise noted. Three folders exist purely to *group* related folders for
navigability -- `Composition/`, `Platform/`, and `Framework/` -- and carry no
code and no namespace of their own; every namespace inside them is still
`PCMode.Tray.<LeafFolder>` (e.g. `PCMode.Tray.Shell` for a type in
`Composition/Shell/`), not `PCMode.Tray.Composition.Shell`. This is
deliberate: the physical grouping below exists for browsing, while the
namespace stays exactly what a `using` directive or a `<see cref>` already
expects, so grouping folders can be introduced (or a folder moved between
groups) without touching every file that references the type through its
namespace.

The three groups follow the dependency-direction diagram above directly:

- **`Composition/`** -- the "App / Shell / Navigation" tier: everything that
  builds, hosts, and presents the app's chrome and startup flow, directly
  serving `App.xaml.cs`. Contains `Shell/`, `Navigation/`, `Runtime/`,
  `Setup/`.
- **`Platform/`** -- process/OS-facing infrastructure, consumed by the
  composition root (and, for `Settings/`, by a couple of features) but not
  itself part of the page-family framework or the engine boundary. Contains
  `Diagnostics/`, `Input/`, `Picker/`, `Themes/`, `Settings/`.
- **`Framework/`** -- the "Controls / Presentation" part of the shared tier:
  the page-family/binding/dialog framework every feature builds on.
  Contains `Controls/`, `Presentation/`, `Layout/`, `Dialogs/`.

`Configuration/` (the "... / Configuration" part of that same tier) and
`Features/` stay at the project root, ungrouped: `Configuration/` is the one
named presentation-to-engine boundary the rest of this document repeatedly
calls out on its own, and `Features/` is already its own clean, independent
tier -- folding either into a group would bury a distinction this document
otherwise goes out of its way to draw. `Assets/`, `Properties/`, and
`App.xaml(.cs)` stay at the root too, as ordinary .NET/WPF project
convention (an icon folder, assembly attributes, and the WPF entry point are
expected there by tooling and by habit alike).

```text
App.xaml(.cs)                    composition root and resources
Composition/
    Shell/                        main window, tray icon/menu, application shell
    Navigation/                   registry, factories, page lifetime
    Runtime/                      process hosting: single-instance, exit, CLI options
    Setup/                        first-run wizard, embedding real feature pages as steps
Platform/
    Diagnostics/                  local log and the one shell-open-folder adapter
    Input/                        global hotkey and controller adapters
    Picker/                       the on-screen picker wheel and its host
    Themes/                       system/light/dark theming, for WPF and the WinForms tray menu
    Settings/                     ApplicationSettingsStore -- tray-side app settings, not mode configuration
Framework/
    Controls/PageShells/          page-family contracts
    Controls/Components/          reusable WPF controls
    Presentation/                 shared ViewModel and binding primitives
    Layout/                       shared resources and templates
    Dialogs/                      dialogs used by multiple features
Configuration/                    engine-facing configuration boundary
Features/<Feature>/               feature page, ViewModel, and local dialogs
Assets/                           application icon (embedded resource + source art)
Properties/                       assembly-level attributes
```

### `App.xaml` / `App.xaml.cs`

The composition root (see "Composition and navigation" above). `App.xaml`
declares `ShutdownMode="OnMainWindowClose"` (overridden in practice -- the
tray icon, not the window, is what keeps the process alive; see
`Composition/Shell/MainWindow`) and merges the shared resource dictionaries in a fixed
order: `Framework/Layout/Styles.xaml`, then `Framework/Layout/PageShellLayout.xaml`, then
`Framework/Layout/ComponentLayout.xaml`, then `Framework/Layout/ConfigurationPageLayout.xaml`,
then `Platform/Themes/Light.xaml`, then `Platform/Themes/Dark.xaml`. `App.xaml.cs` owns every field the process needs
for its whole life (`_runtime`, `_theme`, `_picker`, `_trayIcon`, etc.) and
every method that reacts to something only the composition root can react
to (`ActivateRuntime`, `OnSettingsSaved`, `AskAboutExit`, `ShutdownForReal`).

### `Composition/Shell/`

The application chrome: the one main window and the tray icon/menu that
outlive it.

- **`MainWindow.xaml` / `.xaml.cs`** -- the navigation list (bound to
  `NavigationRegistry.Items`, indented for child pages) plus a
  `ContentControl` that shows whichever page `Composition/Navigation/ShellPages` says is
  active. Hides instead of closing (`AllowClose` gate) so the red X does not
  end the process -- only `App.ShutdownForReal` may set `AllowClose = true`.
- **`PCModeIcon.cs`** -- loads the embedded `Assets/PCMode.ico` as a WinForms
  `Icon` for the tray. Nothing else reads `Assets/`.
- **`TrayMenu.cs`** -- builds the native `ContextMenuStrip` from a
  `TrayMenuState`/`TrayMenuActions` pair read straight off `ModeEngine`
  (there is no `ShellViewModel` to read instead -- see "Permanently
  excluded"): one item per configured target grouped by Space, plus Show
  Picker (when the picker started), Open, and Exit.

### `Composition/Runtime/`

Process-level hosting concerns that exist independently of what page is
open.

- **`SingleInstance.cs`** -- a named mutex plus an auto-reset event; the
  owner process raises `ActivationRequested` when a second launch asks to
  be shown instead. Mutex name is `Local\PCMode.Shell.SingleInstance`.
- **`ExitCoordinator.cs`** -- refuses to let Exit kill a transition in
  flight. Takes `Func<bool> isIdle` (checked against `ModeEngine.IsBusy`
  directly, per "Permanently excluded" -- there is no shell-wide status view
  model to ask instead), and a callback to ask the user what to do when busy.
- **`TrayOptions.cs`** -- parses the command line into a `TrayOptions`
  record (`--background`, `--no-input`, `--no-controller`, `--data-root`,
  `--picker-monitor`, `--favorite`) and separately `Resolve`s those as
  *runtime* overrides layered on top of persisted `ApplicationSettings`
  (never written back). An argument it does not recognize is refused with
  the full list of valid options, not silently ignored -- see the type's own
  remarks for why silent tolerance is worse than a startup refusal here.

### `Composition/Navigation/`

The one place a page's identity, position, and construction are declared.

- **`Navigation.cs`** -- the `PageId` enum and `NavigationRegistry.Items`,
  a flat list of `NavigationItem(PageId, Label, Parent)` records. No page
  owns its own place in the tree.
- **`IPageLifecycle.cs`** -- the two-method `OnActivated`/`OnDeactivated`
  contract every navigable page (whether via `ConfigurationPageShell` or
  implemented directly) satisfies.
- **`PageCatalog.cs`** -- the `Dictionary<PageId, Func<FrameworkElement>>`
  that constructs every feature page and ViewModel, built from one
  `IConfigurationWorkspace` plus the optional process capabilities
  (`party`/`runtime`/`log`/`settingsStore`/`openSetupWizard`/`onSettingsSaved`)
  only some pages need. Also owns `Validate(...)`, which checks
  `NavigationRegistry.Items` for duplicate pages, missing or cyclic parents,
  and pages with no registered factory.
- **`ShellPages.cs`** -- lazily creates and caches one page instance per
  `PageId`, and is the only thing that calls `IPageLifecycle` for ordinary
  navigation (`Activate` deactivates whatever was active, then activates the
  requested page).

### `Configuration/`

- **`ConfigurationWorkspace.cs`** -- defines `IConfigurationWorkspace`, the
  one presentation-to-engine boundary every configuration page depends on
  (`Read`/`ReadProviders`/`Apply`/`Note`/`Warn`/`Fault`; see "Configuration
  pages" above), and its concrete implementation, built once by `App` via
  `ConfigurationWorkspace.For(root, log, party, activate: ActivateRuntime)`.

### `Platform/Settings/`

Tray-side *application* settings -- how the tray itself behaves (theme,
picker, autostart) -- kept separate from mode *configuration* (what a target
does), which lives entirely in `PCMode.Engine` and is read/written through
`Configuration/ConfigurationWorkspace` instead.

- **`ApplicationSettings.cs`** -- the settings record itself (`Theme`,
  `PickerMonitor`, `Favorites`, `DirectShortcuts`, `ControllerEnabled`,
  `StartWithWindows`, `StartMinimizedToTray`, `LaunchBehavior`,
  `StartupTarget`, `SetupCompleted`), with `DefaultFor`/`Validate` against a
  `TargetCatalog`.
- **`ApplicationSettingsStore.cs`** -- reads/writes the `application`
  section of `config.json`, including legacy-shape fallbacks (an old
  `mode` field read as `target`, legacy hotkeys migrated to direct
  shortcuts); `Save` validates, updates the Windows Run-key autostart entry,
  and rolls the autostart change back if the write itself fails.
  `SynchronizeAutoStart` repairs a stale Run-key value at every startup;
  `MarkSetupCompleted` persists only the wizard's completion flag.
- **`AutoStartRegistration.cs`** -- `IAutoStartRegistration` and its
  `WindowsAutoStartRegistration` implementation (an HKCU `Run` key, no
  elevation needed), plus `AutoStartCommand.Build` for the exact command
  line it writes there.

### `Platform/Diagnostics/`

Process-level logging and the one adapter that may open a shell folder for
the user -- kept alongside `LocalLog` rather than under `Features/Diagnostics`
because both are process-level infrastructure `App.xaml.cs` and other
folders depend on directly, not something specific to the Diagnostics page.

- **`LocalLog.cs`** -- a small, dependency-free, size-rotated logger
  (`pcmode.log` plus a bounded number of archives) with `Information`/
  `Warning`/`Error` and `ReadRecentLines`. Built first, before anything else,
  so the three process-wide unhandled-exception handlers in
  `App.OnStartup` have somewhere to write.
- **`ShellFolderOpener.cs`** -- the single boundary allowed to reveal a
  folder in Windows Explorer (`Process.Start` with `UseShellExecute = true`
  behind a static `Open(path)`). `Features/Diagnostics/DiagnosticsViewModel.OpenLogFolder`
  is its only caller, following the same narrow-adapter pattern (see "Native
  Windows... operations belong behind narrow interfaces" in the repository
  root `README.md`) rather than calling `Process.Start` directly from a
  ViewModel.

### `Platform/Input/`

The two global input surfaces, isolated in their own folder so "what in
this process can claim the keyboard or a controller" stays a small,
auditable answer.

- **`GlobalHotkeys.cs`** -- `IHotkeyRegistrar`/`GlobalHotkeys`, an
  all-or-nothing `RegisterHotKey` claim (if any binding is already taken by
  another program, none are registered) with rollback, plus
  `BindingsFor(shortcuts)` to build the picker's compass bindings and one
  Ctrl+Alt+letter binding per configured direct shortcut.
- **`ControllerPoller.cs`** -- `IGamepadReader`/`ControllerPoller` (sticky
  active-slot XInput polling, so a second idle controller cannot steal focus
  from the one already driving the picker) and `XInputReader`, which probes
  `xinput1_4`/`1_3`/`9_1_0` via `NativeLibrary` so the picker still works on
  a machine missing the newest one.

Both are consumed only by `Platform/Picker/PickerHost`, which owns the message-only
window they register against.

### `Platform/Picker/`

The on-screen "wheel" a global hotkey or controller button opens, and
everything needed to host it as a real, positioned, topmost window.

- **`PickerHost.cs`** -- the composition-root seam for the picker: owns the
  message-only `HwndSource`, `Platform/Input/GlobalHotkeys`, `PCMode.Picker`'s
  `PickerCoordinator`, `PickerWindow`, and the optional controller poller;
  `TryStart` registers hotkeys all-or-nothing and returns null with a reason
  on refusal rather than throwing, since a hotkey conflict is not a startup
  failure (see "Composition and navigation" above). Reads and applies a
  target through a `PickerHostActions` record rather than a concrete
  `ShellViewModel`, the same seam `Composition/Shell/TrayMenu` uses.
- **`PickerWindow.cs`** -- the transparent, topmost, `SetWindowPos`-placed
  window that translates keyboard/mouse/relayed-controller input into
  `PickerCoordinator` calls; hides rather than closes until the whole shell
  shuts down.
- **`PickerCanvas.cs`** -- the `FrameworkElement` that paints the wheel
  (wedges, hub, labels, icons) purely from `PickerCoordinator` state, with
  an `AutomationPeer` reporting a live accessible name.
- **`PickerIcons.cs`** -- the static vector-drawn icon set the canvas draws
  from, keyed by `FavoriteIcon` rather than by name.
- **`MonitorLayout.cs`** -- resolves which monitor's work area and DPI scale
  the picker should open at (`GetDpiForMonitor`, with a 1920×1080@100%
  fallback), given a `PickerMonitorChoice` (defined in `Platform/Settings/`, not
  here, since it is a persisted setting).

### `Platform/Themes/`

System/light/dark theming for both the WPF surface and the one WinForms
surface (the tray's `ContextMenuStrip`) that cannot read WPF resources
directly.

- **`ApplicationThemeService.cs`** -- reads the Windows personalization
  registry key for the system preference (`WindowsSystemThemePreference`,
  which also raises `Changed` on `SystemEvents.UserPreferenceChanged`),
  resolves System/Light/Dark against it, swaps `Framework/Layout/Styles.xaml`'s 13
  semantic keys to the resolved palette, and sets each window's native
  title bar via `DwmSetWindowAttribute`. Raises `ThemeChanged` for anything
  else that needs to react (the tray menu).
- **`Light.xaml`** -- the alternate (light) 13-key palette, `Light.`-prefixed
  to coexist with `Framework/Layout/Styles.xaml`'s dark-by-default unprefixed keys; see
  "Shared UI ownership" above for how the two are reconciled at runtime.
- **`TrayMenuTheme.cs`** -- bridges the WPF semantic brushes into a WinForms
  `ProfessionalColorTable`/`ToolStripProfessionalRenderer` for the tray's
  `ContextMenuStrip`, since that control tree lives outside WPF's resource
  system entirely.

### `Framework/Controls/PageShells/`

The page-family contracts every full feature page builds on (see "Page
hierarchy" above).

- **`CommonPageShell.cs`** -- the base `ContentControl`: title, description,
  header action content, footer content, a stable default style key.
- **`ListPageShell.cs`** -- adds only the standard scrolling content layout;
  does not implement `IPageLifecycle` itself.
- **`ConfigurationPageShell.cs`** -- the selector/editor/actions/footer
  frame, first-load-vs-reactivate lifecycle, `EnableLiveRefresh`, the
  selector-selection sync, and the `ConfirmDiscardIfDirty`/`PromptAndApply`/
  `ConfirmAndApply` guards (see "Page hierarchy" and "Configuration pages"
  above for the full contract).

### `Framework/Controls/Components/`

Reusable WPF controls with no feature-specific knowledge (see "Shared UI
ownership" above for what each one is for): `PageSection.cs`, `FieldRow.cs`,
`BrowseFileRow.cs`, `RemovableRow.cs`, `ConfigurationSelectorItem.cs`,
`StatusDot.cs`.

### `Framework/Presentation/`

Shared ViewModel and binding primitives every feature depends on.

- **`ObservableObject.cs`** -- the minimal `INotifyPropertyChanged` base
  (`SetProperty`/`OnPropertyChanged`) everything else here builds on.
- **`ConfigurationPageViewModelBase.cs`** -- see "Configuration pages"
  above for its full contract.
- **`ObservableEditableRow.cs`** -- the shared "row with an injected
  onChanged callback" base (see "Configuration pages" above).
- **`IConfigurationSelectorItem.cs`** -- the one-member `object Key`
  contract a selector-bound item implements.
- **`Converters.cs`** -- `StringToVisibilityConverter`, `BoolToVisibilityConverter`
  (with `Invert`), `InverseBooleanConverter`, `IndentMarginConverter`,
  declared as shared resources in `Framework/Layout/Styles.xaml`.

### `Framework/Layout/`

Resource dictionaries only -- no code-behind, no logic (see "Shared UI
ownership" above for what each file defines): `Styles.xaml`,
`PageShellLayout.xaml`, `ComponentLayout.xaml`, `ConfigurationPageLayout.xaml`.

### `Framework/Dialogs/`

- **`AliasPromptWindow.xaml` / `.xaml.cs`** -- the shared name-entry modal
  behind `ConfigurationPageShell.PromptAndApply`; every feature's Create,
  Rename, and Duplicate flow reuses this one window rather than building
  its own.

### `Composition/Setup/`

- **`SetupWizardWindow.xaml` / `.xaml.cs`** -- see "First-run setup wizard"
  above. Not itself a `Features/` folder because it embeds three existing
  feature pages rather than owning any editable state of its own.

### `Features/<Feature>/`

One folder per navigable feature, each independent of every other: a
`<Feature>Page.xaml`/`.xaml.cs` deriving from a page-family shell, a
`<Feature>ViewModel.cs` (deriving from `ConfigurationPageViewModelBase`
where the feature is an editable named collection), and any row types or
feature-local dialogs the page alone needs. Twelve exist today --
Applications, Audio, Cursor, Devices, Diagnostics, Home, Layouts,
Rgb, Settings, Spaces, Television, Theme -- one per `PageId`. See "Page
hierarchy" for which page family each one uses and why; `Devices` is both
the hardware-inspection page (what `Features/Hardware` used to show on its
own page, now folded into `DevicesPage`/`HardwareViewModel`) and the
navigation hub the parent Applications/Audio/Cursor/Rgb/Theme/Layouts/
Television nest under in `NavigationRegistry`.

Two feature areas keep a legacy-vendor-specific name at the engine/domain
level while using a generalized name at the page/ViewModel level, by
design: `Features/Rgb` (`RgbPage`/`RgbViewModel`) sits on top of engine
types still named `CaseRgb*`; `Features/Television` (`TelevisionPage`/
`TelevisionViewModel`) sits on top of engine types still named `LgTv*`
(reflecting that LG is, so far, the only supported television integration).
This is intentional and consistent across both, not a naming drift to
reconcile.

### `Assets/`

- **`PCMode.ico`** -- the embedded tray/window icon (`Composition/Shell/PCModeIcon`
  loads it; the `.csproj`'s `ApplicationIcon` also points at it).
- **`PCMode.Icon.svg`** -- the source art the `.ico` was generated from; not
  itself referenced by any code.

### `Properties/`

- **`AssemblyInfo.cs`** -- `[InternalsVisibleTo("PCMode.Tray.Tests")]`
  (so the test project can exercise internal members without a public API
  surface existing only for tests) and the WPF `ThemeInfo` attribute.

## Running the tray

```powershell
dotnet run --project app/src/PCMode.Tray/PCMode.Tray.csproj
```

The tray defaults to the per-user data root `%LOCALAPPDATA%\PCMode\Native`
(from `PCModePaths.DefaultRoot`) and the `config.json`/`state.json` within
it. Point it at an isolated folder with `--data-root <folder>` for manual
testing against a throwaway configuration instead of your real one:

```powershell
dotnet run --project app/src/PCMode.Tray/PCMode.Tray.csproj -- --data-root C:\PCMode-Dev
```

## Adding a page

1. Add a folder under `Features`.
2. Derive the page from `ListPageShell` or `ConfigurationPageShell`. A
   `ConfigurationPageShell` page gets first-load/reactivate handling and
   selection protection for free; a `ListPageShell` page that needs to do
   anything on activation implements `IPageLifecycle` directly.
3. Keep its XAML limited to feature content.
4. For a configuration page, derive its ViewModel from
   `ConfigurationPageViewModelBase` and give selector items a small
   `IConfigurationSelectorItem` type if the page has named, creatable
   entries.
5. Add its `PageId`, navigation entry, and explicit `PageCatalog` factory.
   Only add a new `PageCatalog` constructor parameter if the page needs a
   capability no existing page already threads through (`runtime`, `log`,
   `party`, `settingsStore`, `onSettingsSaved`) -- reuse what's there first.
6. Reuse existing components and layout resources.
7. Add the page to `PageConstructionTests`' per-page type assertions
   (`NavigationTests` and the shell-type check already cover it
   automatically via `NavigationRegistry.Items`/`Enum.GetValues<PageId>()`).

Create a new abstraction only when two or more real pages share the same
structure or behavior. Keep one-feature behavior local.

## Testing conventions

`app/tests/PCMode.Tray.Tests` (listed in `app/PCMode.slnx`, so it runs as
part of `dotnet test app/PCMode.slnx`) is deliberately not a UI test suite --
nothing in it drives the app by simulating keys or clicks. It builds real
pages, ViewModels, and a real `ConfigurationWorkspace` over a temporary
folder directly, and asserts what came out, using an STA thread
(`StaThread.Run`) for anything that touches WPF, joined to
`WpfWindowConstructionCollection` so no two window/BAML-loading tests run
concurrently (loading BAML performs one-time static registration in the
process the first time it runs; two tests doing that at once produced an
intermittent `NullReferenceException`). `PageConstructionTests` runs on a
real WPF `Application` with this project's own merged resource dictionaries
loaded, so a page whose template references a missing shared style fails
there instead of only when someone opens it. `Workspaces.cs` holds the
`IConfigurationWorkspace` fakes and temporary-root helper most tests build
on.

## Architectural checks

Changes should preserve these invariants:

- every navigation entry has one page factory and a valid, acyclic parent;
- full pages derive from a page-family shell, or implement `IPageLifecycle`
  directly with a documented reason not to (Devices, Diagnostics, Settings);
- shared geometry and behavior have one owner;
- feature code does not leak into Shell, Navigation, or shared controls;
- page refresh does not discard unsaved edits;
- native, machine-changing operations (starting a process, opening a shell
  folder, claiming global input) go through a narrow adapter, not straight
  from a ViewModel;
- the project builds with all merged dictionaries and control templates valid.
