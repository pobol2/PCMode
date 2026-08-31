# Development principles

These rules describe the durable boundaries of PCMode. Current subsystem structure belongs in [ARCHITECTURE.md](ARCHITECTURE.md), and live-audio details belong in [AUDIO_ARCHITECTURE.md](AUDIO_ARCHITECTURE.md).

## 1. Desired state has one owner

- Configuration states the target value, not a script, sleep, or UI-authored sequence.
- Space -> Activity -> Source composition belongs in Engine. Missing inherits, a value overrides, and explicit clear removes an inherited assignment.
- Every activation surface resolves through `TargetCatalog` and executes through `ModeEngine`.
- Dependency order, availability, diffing, retries, verification, cancellation, Health, Retry, and Repair are Engine behavior. Do not duplicate them in ViewModels or shell code.

## 2. Preserve dependency direction

- Engine contains no WPF, Windows Forms, Tray, or picker-rendering types.
- Picker behavior contains no window, screen, dispatcher, HWND, or device dependency.
- Tray composes and presents; it does not become another engine.
- The desired-state Engine may use the audio control-plane projects (`Audio.Core`, `Audio.Protocol`, and `Audio.Client`) but must not reference `Audio.Engine`, `Audio.Windows`, `Audio.DriverClient`, or `Audio.Host`.
- The audio Host owns PCM, DSP, buffering, clocks, WASAPI, and the driver transport. Keep it a separate process and a versioned protocol boundary.

## 3. Give every capability one clear owner

- Feature-specific behavior, validation, rows, and dialogs stay in the feature.
- Navigation metadata stays in `NavigationRegistry`; construction stays in `PageCatalog`; page lifetime stays in `ShellPages` and the page families.
- Process-lifetime resources are created and disposed by `App`, not by pages.
- Shared UI moves into Framework only after multiple real pages need the same interaction or layout.
- Add a page only when a capability needs a distinct screen. Extend the existing Displays or Settings ownership instead of recreating separate device, layout, cursor, television, DDC/CI, or appearance pages.

## 4. Keep platform effects narrow and fakeable

- A class that changes Windows, hardware, processes, files, registry, network devices, global input, or the driver must sit behind a focused interface or adapter.
- ViewModels may coordinate adapters but may not add P/Invoke, COM, socket, process, registry, or driver calls.
- Do not silently choose substitute hardware when a configured identity is missing or ambiguous.
- Treat native and device responses as fallible: bound them, validate sizes/values, preserve cancellation where the API permits it, and report actionable diagnostics.
- Avoid shell command strings. Pass executable arguments separately; use the shell only when URI/file association behavior is the intended contract.

## 5. Validate and write configuration as a transaction

- Stable IDs are identity; display names are presentation.
- Validate the complete schema, provider set, hierarchy, references, and candidate values before applying or committing a mutation.
- Write `config.json` atomically and preserve its one-generation backup.
- A page save is incomplete until `IConfigurationWorkspace.Apply` has attempted runtime reload and reported whether the new file became active.
- A failed reload must leave the previous engine active. Never partially replace the provider set.
- Preserve unrelated configuration fields and references during focused edits, renames, duplication, and deletion.
- Update migrations and the worked example when the accepted document shape changes.

## 6. Protect edits and distinguish saved state from live state

- Use the established page family. Named editable collections use `ConfigurationPageShell`; simple read/list pages use `ListPageShell`.
- Use `ConfigurationPageViewModelBase` for selector/dirty/save/discard state rather than reimplementing it per feature.
- Page reactivation and live refresh may update observed state only. They must not reload configuration over unsaved edits.
- Live controls that act immediately on hardware are not configuration writes. Make the distinction visible in the ViewModel and UI.
- Destructive or navigating actions must pass the shared unsaved-change guard.

## 7. Make failure and recovery honest

- A write, dispatch, and verified state are different facts. Report them separately.
- Prefer readback. When a platform cannot be observed reliably, say that the result is unverified; do not invent success.
- Required resource failure stops the target. An optional resource may degrade it without hiding the concern.
- Record the current target only after a completed non-dry-run transition; clear stale state before a transition can move the machine.
- Never log or export secrets. Keep sensitive provider material outside ordinary configuration and protect it with the operating system where available.

## 8. Keep concurrency and lifetime explicit

- One Tray instance owns global input and the tray icon per user session.
- One process-lifetime `AudioHostLink` is shared across engine reloads.
- One Host owns a given audio pipe and one client owns the driver's PCM transport.
- Do not block the UI thread with discovery, DDC/CI, network, audio, or process waits.
- Timers belong to lifecycle-aware owners and stop when their page/process is inactive.
- Shutdown must define what happens to in-flight transitions and to an active audio Host; never rely on object finalizers for that policy.

## 9. Test at the owning boundary

- Engine tests use fake providers and temporary roots.
- Picker tests use numeric/state inputs and no windowing types.
- Tray tests construct real ViewModels/pages with fake workspaces; they do not duplicate Engine tests or drive the desktop by simulated clicks.
- Audio Core/Engine/Windows/DriverClient/Protocol/Host tests live in their corresponding projects.
- Ordinary tests must not change the developer machine. Real-hardware tests are opt-in, narrowly filtered, explicit about effects, and restore state on every path as far as the platform permits.
- Add focused contract tests when changing navigation, page construction/lifecycle, protocol compatibility, configuration migration, native structure layout, or security boundaries.

## 10. Keep releases auditable

- Warnings are errors; keep changes focused and deterministic.
- Do not add elevation, a service, driver access, network listener, external process, or dependency incidentally.
- Update README, architecture documents, SECURITY, tests, diagnostics, and THIRD_PARTY_NOTICES when their current contract changes.
- Audit the actual publish/driver artifacts, licenses, signatures, and notices before release; project references alone are not a release inventory.
