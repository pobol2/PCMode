# PCMode

PCMode is a native Windows application that configures a computer and its associated AV environment around what the user wants to do. It coordinates displays, audio, applications, TVs, controllers, and other supported resources as one verified transition.

PCMode is proprietary software under active development. Product sequence and release scope live in the [product roadmap](PCMode_Product_Roadmap.md); this document describes the stable architecture and development model.

## Product model

The long-term user-facing model is:

```text
Space → Activity → Source
```

- **Space** describes where the setup is being used.
- **Activity** describes what the user wants to do.
- **Source** describes what provides the experience.

These layers compose into a desired resource state. The orchestration engine determines dependencies and meaningful differences, performs the transition, reads state back where possible, and exposes failures through Health and Repair.

```text
User interface or activation surface
                 ↓
    Activate selected user intent
                 ↓
      Compose desired state
                 ↓
 Plan dependencies and transitions
                 ↓
    Capability-driven providers
                 ↓
 Read → Apply → Verify → Health / Repair
```

Current configuration concepts are expected to evolve through versioned migrations rather than by replacing the orchestration engine.

## Architecture principles

- **Desired state over scripts.** Configuration expresses target values, not sleeps or ordered command macros.
- **One activation path.** Tray, picker, controller, CLI, and future integrations must use the same orchestration contract.
- **Diff before apply.** Resources that already match the requested state should remain untouched.
- **Capability-driven providers.** Providers advertise and validate only the operations their platform or device supports.
- **Verification where possible.** A successful write is not treated as proof when reliable readback exists.
- **Explicit dependencies.** Transition ordering comes from resource requirements rather than UI code.
- **Safe convergence.** Timeouts, cancellation, retries, and newer requests are handled at well-defined resource boundaries.
- **Stable logical identity.** User-facing aliases remain separate from hardware identifiers that may change.
- **Required versus optional resources.** An unavailable optional integration must not invalidate an otherwise usable target.
- **Recoverable configuration.** Schema versions, validation, backups, and migrations protect user state.
- **Local first.** The application runs as the logged-in user; remote coordination is not assumed by the core engine.

## Solution structure

```text
app/PCMode.slnx
├── src/PCMode.Engine/       configuration, discovery, planning, providers,
│                            convergence, state, health, and native adapters
├── src/PCMode.Picker/       UI-independent picker geometry and interaction
├── src/PCMode.Tray/         WPF shell, pages, tray icon, input, and startup
└── tests/                   deterministic tests for each project
```

Dependencies point toward lower-level behavior:

- `PCMode.Engine` contains no WPF or Windows Forms UI types.
- `PCMode.Picker` contains no windowing dependency.
- `PCMode.Tray` composes the engine and picker into the desktop application.
- Native Windows, device, filesystem, process, and network operations belong behind narrow interfaces.
- Tests use fakes for machine-changing boundaries and must not manipulate the test machine's real displays, audio devices, registry, processes, controllers, or network hardware.

`PCMode.Engine.Tests` and `PCMode.Picker.Tests` enforce that those two projects stay free of WPF/windowing dependencies; see each project's own test conventions.

## Provider model

A resource provider participates in the orchestration lifecycle as its capabilities allow:

```text
Discover → Validate → Read → Apply → Verify
```

Providers also report availability, dependencies, timeouts, cancellation behavior, and diagnostic health. Platform-specific compatibility work stays inside the provider or its native adapter; intent composition and UI code should not depend on native implementation details.

New machine-changing behavior must:

1. sit behind a fakeable interface;
2. define validation and availability behavior;
3. participate in dependency planning;
4. provide readback and verification where the platform permits;
5. expose actionable failures and diagnostics;
6. include deterministic tests that do not require real hardware.

## GUI architecture

`PCMode.Tray` is composed rather than built inline in startup, using explicit construction, page-family shell controls, and one folder per feature so shared UI stays consistent without a container or reflection. `App.xaml.cs` is the composition root and the only place that constructs the pieces below; a change to one page's layout or navigation position should only ever touch that page's own `Features/<Area>/` folder plus the navigation registry.

- **Navigation registry** -- `Composition/Navigation/Navigation.cs`. The one place pages, their order, their labels, and their parent/child relationships are declared. Adding, removing, or reordering a page is a change to this file and nothing else.
- **Page construction and lifetime** -- `Composition/Navigation/PageCatalog.cs` builds each page's View and ViewModel together; `Composition/Navigation/ShellPages.cs` creates pages lazily the first time they're opened, caches one instance per page, and drives `IPageLifecycle.OnActivated`/`OnDeactivated` on navigation.
- **Shared UI services** -- `Configuration/ConfigurationWorkspace.cs` (`IConfigurationWorkspace`). The things every editing page needs: where the configuration lives, its currently configured providers, a way to make a save take effect, and somewhere to report what happened. Deliberately not a general service locator: a page that needs something else takes that as its own constructor argument instead of growing this interface.
- **Page-family shells** -- every feature page derives from `ListPageShell` or `ConfigurationPageShell` (`Framework/Controls/PageShells/`), which own the shared frame, actions, Save/Discard handling, and selection protection so a feature page supplies only its own content and bindings.
- **Shared resources** -- `Framework/Layout/Styles.xaml` (shared control/layout styles and the semantic color palette), `Platform/Themes/` (System/Light/Dark resolution), and `Framework/Presentation/` (shared ViewModel and binding primitives).
- **Expected folder structure per GUI area** -- one folder per feature under `Features/<Area>/`, containing that area's `<Area>Page.xaml` + `<Area>Page.xaml.cs` and `<Area>ViewModel.cs`.

See `app/src/PCMode.Tray/ARCHITECTURE.md` for the full composition, navigation, and page-family reference. `app/tests/PCMode.Tray.Tests/NavigationTests.cs` and `PageConstructionTests.cs` enforce the registry-matches-catalog invariant in automated tests.

## Test architecture

Each test project owns exactly the source project it tests, and its own folders mirror that project's boundaries so a new test has one obvious home.

```text
tests/PCMode.Engine.Tests/   one folder per Engine folder -- Convergence, Discovery,
                             Intent, Planning, Providers/<Domain>. Cross-cutting
                             engine-level rules (ModeEngineTests, EngineRuntimeTests,
                             HealthTests, ConfigurationWriterTests, ...) stay at the
                             project root.
tests/PCMode.Picker.Tests/   flat. The picker's own source is six files; folders
                             would organize nothing.
tests/PCMode.Tray.Tests/     flat. Not a UI test suite -- nothing in it drives the
                             app by simulating keys or clicks. It builds real
                             pages, ViewModels, and a real ConfigurationWorkspace
                             over a temporary folder directly, and asserts what
                             came out.
```

- **Shared fakes and fixtures** stay small and live beside the tests that need them rather than in one shared library. `PCMode.Engine.Tests/FakeProvider.cs` holds a misbehaving resource and `TemporaryRoot`, the temporary configuration folder most Engine tests build on. `PCMode.Tray.Tests/Workspaces.cs` holds the `IConfigurationWorkspace` fakes and temporary-root helper most Tray tests build on. Provider-specific fakes (`FakeAudioEndpoints`, `FakeDisplayDevices`, `FakeTelevision`, `FakeProcessHost`, and similar) live in their own provider's test folder instead of being generalized into something every test depends on.
- **The serialized WPF collection** -- `PCMode.Tray.Tests/StaThread.cs`. WPF requires STA, and xunit gives no guarantee about the apartment of the thread a test runs on, so any test that builds a real window or page runs its body through `StaThread.Run` on a fresh STA thread. Building a window also loads BAML through WPF's pack-URI resource machinery, which performs one-time static registration the first time it runs in the process; two of those tests doing that at once, on their own STA threads, produced an intermittent `NullReferenceException` inside `System.IO.Packaging.PackagePart.GetStream`. Every test that builds a real window or page therefore also carries `[Collection(WpfWindowConstructionCollection.Name)]`, which tells xunit never to run two of them concurrently. Every other test in the assembly still runs fully parallel; a test that only builds plain WPF objects (no window, no page, no pack-URI resource) uses `StaThread.Run` without joining the collection.

```powershell
dotnet test app/PCMode.slnx --no-build
```

## Configuration and local data

PCMode owns a per-user data directory under:

```text
%LOCALAPPDATA%\PCMode\Native
```

The engine derives all configuration, state, log, backup, and diagnostic paths from an overridable root so tests never write to the real user profile. Configuration is versioned (`schemaVersion`) and validated before it becomes active; an unknown key or an unusable value is refused by name, before anything is changed. Raw hardware identifiers remain provider details; user-facing configuration should prefer stable logical aliases.

Every configuration write goes through `ConfigurationWriter.WriteAtomically` (`app/src/PCMode.Engine/ConfigurationWriter.cs`): the candidate is written to a temporary file and put in place with a single filesystem rename, and whatever the file held immediately before the write becomes its `.bak` in that same atomic step. This is a one-generation backup, not a history -- it protects against a candidate that validated cleanly but is wrong once active, or a save someone wants undone a moment later. There is no GUI restore action yet (tracked for a later roadmap step); to recover manually, close PCMode and copy `config.json.bak` over `config.json` under the data directory above.

The worked development example is `app/config/pcmode.example.json`. Local configuration and state files must not be committed.

## Troubleshooting

- **Logs** -- the Diagnostics page in the tray shows and opens the current log folder (`%LOCALAPPDATA%\PCMode\Native\logs`).
- **A saved change did not take effect** -- check the page's own validation message first; an invalid edit is rejected before it reaches disk. If a save appears to have succeeded but the running state looks wrong, use Repair where the resource is shown, or restart PCMode to force a fresh read.
- **Configuration will not load** -- see the backup/recovery paragraph above; PCMode does not yet fall back to a recovery UI when `config.json` itself fails to parse (see Known limitations).
- **A device is missing or behaves unexpectedly** -- check whether it is marked required or optional in its Space/Activity/Source; an unavailable optional integration should not block unrelated resources.

## Known limitations

These are intentionally deferred by the [product roadmap](PCMode_Product_Roadmap.md) rather than defects:

- No in-GUI configuration backup/restore, reset, or sanitized support-bundle export yet (Step 13); recovery from a bad save is currently the manual `.bak` copy described above.
- The Diagnostics page currently only opens the log folder; it does not yet aggregate health findings or generate a support bundle.
- Audio Targets beyond the existing Party mode (named single/two-output targets, Equalizer APO profiles) are not implemented yet (Step 7).
- External HDMI Sources, DDC/CI monitor control, and guided onboarding are not implemented yet (Steps 8, 10, 11).

## Build and test

Requirements:

- Windows 10 or 11;
- the .NET 10 SDK;
- no physical TV, special monitor, audio device, or controller is required for automated tests.

From the repository root:

```powershell
dotnet restore app/PCMode.slnx
dotnet build app/PCMode.slnx --no-restore
dotnet test app/PCMode.slnx --no-build
```

Run the desktop application during development with:

```powershell
dotnet run --project app/src/PCMode.Tray/PCMode.Tray.csproj
```

Warnings are treated as errors. Code should follow `.editorconfig`, keep changes focused, and comment on design constraints rather than narrating obvious statements.

## Manual and hardware verification

The automated suite is deliberately hardware-free (fakes stand in for every device and native boundary). The following can only be verified by hand, against real hardware, on the machine running this checklist:

- **Displays** -- enablement, primary display, placement, and refresh rate actually change on the physical monitors, and Repair recovers a display disconnected mid-session.
- **Audio and Party mode** -- normal endpoint switching, and a two-device Party session: reference/mirror selection, live delay/level calibration, drift over an extended session, and clean shutdown/ownership handoff.
- **Applications** -- launch, process detection, readiness, focus/reactivation, and stop behavior for a real installed application.
- **LG TV** -- discovery/pairing, power, input switching, and any configured profiles/advanced settings against a real TV.
- **RGB (OpenRGB)** -- scene activation against real controllers, and behavior when the OpenRGB server is not running.
- **Cursor, theme, and Do Not Disturb** -- applied state matches what Windows reports afterward.
- **Picker and controller** -- the on-screen picker and any configured controller/hotkey activation, including controller disconnect/reconnect.
- **Missing/renamed/unavailable hardware** -- a Space or Activity that references a device no longer present fails clearly without blocking unrelated resources.
- **Restart and reload** -- exiting and relaunching PCMode reloads the last-active configuration and state correctly.

Party mode's hour-long two-device drift/underrun soak and the native-interop disposal paths in `WasapiAudioMirror`/`WasapiTestSignal`/`CoreAudioEndpoints` are exercised by `PartyHardwareTest` and `HealthHardwareTest` in `PCMode.Engine.Tests` (gated, skipped in normal runs) rather than by this checklist alone; run them directly against real playback devices when verifying audio changes.

## Development rules

- Do not introduce a UI dependency into the engine or picker behavior projects.
- Do not call machine-changing APIs directly from orchestration or view-model code.
- Keep undocumented or version-sensitive platform APIs isolated behind compatibility boundaries.
- Do not silently select substitute hardware when a configured device is missing.
- Avoid dependencies on external scripts or helper applications for core behavior.
- Do not add elevation, a Windows service, or a custom driver without an explicit architectural and release decision.
- Update the example configuration, migrations, tests, diagnostics, and third-party notices when a change genuinely affects them.
- Treat the [product roadmap](PCMode_Product_Roadmap.md) as the source of implementation order, not as runtime documentation.

## Security and privacy

PCMode is designed to run with the current user's permissions and to communicate only with configured providers and release services. Sensitive provider credentials must use operating-system protection where available and must never appear in configuration exports, logs, or support bundles.

See [SECURITY.md](SECURITY.md) for reporting and security expectations.

## Distribution and legal

PCMode is proprietary and is not licensed as open-source software. Access to this repository does not grant permission to copy, modify, or redistribute its contents. See [LICENSE](LICENSE).

Third-party software and interoperability notices are documented in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
