# PCMode system architecture

This document explains how the application fits together. For presentation and process composition, see [PCMode.Tray architecture](src/PCMode.Tray/ARCHITECTURE.md). For the native audio data plane, see [Audio architecture](AUDIO_ARCHITECTURE.md).

## System shape

PCMode is a desired-state controller for one signed-in Windows user. Configuration describes named targets; the Engine composes each target into resource values and converges the machine on them. The Tray provides every interactive activation surface but does not implement a second transition path.

```text
Home / tray / picker / hotkey / controller / startup
                         |
                         v
                 EngineRuntime.Engine
                         |
                         v
        TargetCatalog -> ModeEngine -> providers
                                      |  |  |
                                      |  |  +-- applications, RGB, DDC/CI
                                      |  +----- displays, cursor, LG TVs
                                      +-------- Windows audio / Audio Host
```

The application is split into three kinds of code:

- `PCMode.Engine` owns configuration, target composition, transition planning, provider coordination, convergence, persistent target state, and provider diagnostics.
- `PCMode.Picker` owns window-independent picker selection, layout, easing, favorites, and controller translation.
- `PCMode.Tray` owns the WPF process, pages, setup, tray icon, picker rendering, global input, and application settings.

Audio adds a separate process and native boundary. `PCMode.Audio.Host` owns real-time audio; the ACX/KMDF driver creates the virtual render endpoint. Engine communicates with Host only through `PCMode.Audio.Client` and `PCMode.Audio.Protocol`.

## Dependency direction

```text
PCMode.Tray ------> PCMode.Engine ------> Audio.Core
     |                    |              Audio.Protocol
     |                    `------------> Audio.Client
     `------------> PCMode.Picker

PCMode.Audio.Host -> Audio.Engine -> Audio.Core
                  -> Audio.Windows
                  -> Audio.DriverClient -> native ACX/KMDF driver
                  -> Audio.Protocol
```

Engine contains Windows provider implementations, so it targets Windows, but it has no WPF or Tray dependency. Picker and the audio Core, Engine, Protocol, and Client projects remain window-independent where their responsibilities allow it. Tray has a build-order-only reference to Host and copies the complete Host output into `AudioHost/`; it cannot call Host types in process.

## Space, Activity, Source, and resources

The current schema is version 1 and requires a hierarchy:

```text
Space -> Activity -> optional Source
```

- A Space groups the resources shared by a physical or logical place. It is not activatable.
- Every enabled Activity is an activatable target.
- Every enabled Source is another activatable target below its Activity.
- Source kind (`pcApplication`, `externalDevice`, or `tvApplication`) is descriptive metadata. Resource assignments, not the kind, perform the transition.

Stable IDs form canonical keys such as `desk/gaming` and `living-room/movie/console`. Display names are mutable and form human-readable paths. `TargetCatalog` resolves a canonical key, a full display path, or an unambiguous leaf name; ambiguous names are rejected.

Resource assignments compose from outer to inner:

- missing: inherit the parent assignment;
- value: override the parent value;
- explicit `null`: clear the inherited assignment;
- required/best effort: determine whether failure blocks the target.

Composition produces one desired value per resource. It does not produce an ordered script. The provider graph determines transition order.

## Engine and provider model

Each machine capability is an `IModeResourceProvider` with a unique resource key. Configurable providers validate their section of `config.json`. Providers may declare ordering preferences, requirements for particular values, availability, convergence policy, readback, and health details.

`ModeEngine` performs a transition as a coordinated operation:

1. Resolve and compose the target.
2. Validate resource keys and values against the loaded providers.
3. Expand value-specific requirements and topologically order the provider graph.
4. Check availability and omit unavailable best-effort resources.
5. Read observable current values and diff them from desired values.
6. Apply changed resources using each provider's retry, polling, settle, and verification policy.
7. Produce `ModeHealth`, including failure, retry, and repair information.
8. Record the current target key only after a completed, non-dry-run transition.

Only one transition runs at a time. A dry run plans and diagnoses without applying providers or updating state. Required failure prevents the target from being recorded; best-effort failure is visible as degradation.

The production provider inventory is built explicitly by `EngineBootstrap`:

- Windows theme, display topology, cursor position, audio, Do Not Disturb, and case RGB;
- four resources for each configured LG television: power, input, signal settings, and picture profile;
- brightness and input resources for each configured DDC/CI monitor;
- one resource for each configured application.

Provider identity and ordering stay in Engine. Pages may discover, edit, and invoke providers, but they do not reproduce dependency planning or convergence.

## Runtime and configuration reload

`EngineRuntime` exposes the currently active `ModeEngine`. A reload builds and validates a complete candidate engine from disk before swapping it in. Reload is deferred while a transition is active, and a failed candidate leaves the previous engine active.

`AudioHostLink` has Tray-process lifetime and is passed to every candidate engine. This prevents an engine reload from orphaning or competing with a live Host. Disposing the Tray-side link does not shut down Host; an active audio target must continue playing after the shell exits.

All activation surfaces fetch the live engine at invocation time. They must not retain a provider or target catalog from a replaced engine.

## Major integrations

### Tray, picker, and controller

The WPF shell has eight pages: Home, Spaces, Displays, Audio output, Case lighting, Applications, Settings, and Diagnostics. Displays is the single owner of layouts, cursor placement, monitor registration, DDC/CI, and television association. Tray page construction, lifecycle, setup, input, and UI ownership are detailed in [the Tray architecture](src/PCMode.Tray/ARCHITECTURE.md).

The picker coordinator and controller translation live in `PCMode.Picker`; Tray supplies the window, monitors, hotkey registration, XInput polling, and activation delegate. Picker, direct hotkeys, controller selection, Home, and the tray menu all call the same live Engine.

### Displays, DDC/CI, and televisions

Display aliases map to stable Windows monitor device paths. A layout specifies enabled monitors, primary monitor, relative placement, optional refresh rates and dependencies, and an optional cursor-position link.

Each configured DDC/CI monitor contributes `ddc.<alias>.brightness` (VCP `0x10`) and `ddc.<alias>.input` (VCP `0x60`). The provider discovers physical monitors, parses capability strings, bounds native calls, reads back where supported, and accepts known or raw input values. DDC/CI support remains hardware-, connection-, and firmware-dependent.

Each configured LG webOS television owns one serialized connection and four resources. The default television uses `tvPower`, `tvInput`, `tvSignal`, and `tvProfile`; additional televisions use `tv.<name>.*`. Pairing begins over secure WebSocket with the on-TV prompt, then pins the observed certificate fingerprint. The client stores the key and fingerprint with current-user DPAPI. Power-on uses bounded Wake-on-LAN attempts; command responses are size-bounded.

### Audio

The `audio` resource accepts either a direct Windows output alias or an audio-target name. Direct aliases stop any active Host graph and make a physical endpoint the Windows default. Audio targets start a multi-route Host graph and then make the PCMode virtual endpoint the default. The complete data and control flow is in [Audio architecture](AUDIO_ARCHITECTURE.md).

### Applications and remaining providers

Application resources can launch executables without shell expansion or intentionally dispatch a configured URI through the shell. They observe readiness/reactivation and may stop configured processes. Guarded key injection is allowed only after the intended process owns the foreground window.

Case lighting connects to an already-running OpenRGB server on loopback. Cursor, Windows theme, Do Not Disturb, and endpoint-default changes remain independent Engine resources and compose through the same target model.

## Configuration and persistence

The default per-user root is `%LOCALAPPDATA%\PCMode\Native`; `--data-root` selects another root for development or isolation.

| Path | Owner and purpose |
|---|---|
| `config.json` | Schema-versioned application, provider, and Space hierarchy configuration. |
| `config.json.bak` | One-generation backup from an atomic configuration write. |
| `state.json` | Last successfully recorded target key and timestamp. |
| `logs\pcmode.log` | Current bounded log; two rotated archives are retained by default. |
| `lgtv\` | Per-television DPAPI-protected pairing material. |
| `config.before-audio-targets.json` | One-time backup created if the audio-target migration is required. |

JSON comments and trailing commas are accepted. Feature stores validate the complete candidate and preserve unrelated fields while editing their owned section; `ConfigurationWriter` then writes it atomically. `ConfigurationWorkspace` asks `EngineRuntime` to activate the saved configuration and reports file success separately from runtime activation or deferral.

Application settings share `config.json` under `application`: appearance, picker/favorites, direct shortcuts, controller, startup behavior, and setup completion. Windows autostart is synchronized through the current user's Run key.

## Diagnostics and recovery

Diagnostics presents the live target catalog, current target, busy state, dry-run result, last `ModeHealth`, resource details, loaded providers, version/data paths, and bounded recent log. It does not apply repairs independently; Retry and Repair remain Engine operations.

Recovery occurs at the narrowest owning boundary. Providers use convergence policies for delayed or contested hardware. Engine preserves the previous configuration if reload fails. The Audio Host retains its desired target and reconnects failed input/output routes with bounded backoff. LG credentials and certificate pins survive reconnects; DDC/CI failures remain explicit because the platform cannot make unsupported hardware reliable.

## Guided setup

The modal wizard uses fresh instances of the real Displays, Audio, Spaces, and Settings pages. Those pages retain their normal validation, write, and runtime-reload paths; there is no parallel setup configuration model. First-run setup starts at Welcome. Reopening resumes at Displays when no layout exists, Spaces when layouts exist but no targets exist, and Finish otherwise. Next and Finish do not activate a target.

## Build and verification

The managed solution, native driver, automated tests, and opt-in hardware tests have different prerequisites and safety profiles. See [Build and test guide](BUILD_AND_TEST.md). Security and privileged boundaries are documented in [the repository security policy](../SECURITY.md).
