# PCMode

PCMode is a Windows tray application that converges a PC and its attached AV environment on a named target. One activation can coordinate display topology, audio routing, applications, cursor position, Windows appearance and notifications, OpenRGB lighting, DDC/CI monitor controls, and LG webOS televisions.

PCMode is proprietary software. See [LICENSE](LICENSE), [SECURITY.md](SECURITY.md), and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Core model

Configuration is organized as:

```text
Space -> Activity -> optional Source
```

A Space supplies shared resource values. Every Activity is an activatable target, and each Source adds another target below its Activity. Values inherit from Space to Activity to Source; an override replaces the inherited value and explicit `null` clears it. Source kind describes whether a source is a PC application, external device, or television application, but resource assignments perform the transition.

Every activation surface—Home, tray menu, picker, hotkey, controller, and startup—resolves through the same `TargetCatalog` and invokes the same `ModeEngine`. Engine composes desired resources, plans provider dependencies, checks availability, reads and diffs observable state, applies with provider-specific convergence, verifies where possible, and records the current target only after a completed transition.

## Application

The current navigation is:

```text
Home
Spaces
  Displays
  Audio output
  Case lighting
  Applications
Settings
Diagnostics
```

- **Home** activates favorites and browses Spaces.
- **Spaces** edits the Space -> Activity -> Source hierarchy, inheritance, required/best-effort resources, picker placement, and direct shortcuts.
- **Displays** owns display layouts, cursor placement, monitor registration, DDC/CI brightness/input, and LG television association/configuration.
- **Audio output** owns direct Windows output aliases plus native multi-route audio targets and processing profiles.
- **Case lighting** owns OpenRGB scenes.
- **Applications** owns executable/URI launch, readiness, reactivation, observation, and stop behavior.
- **Settings** owns appearance, picker monitor, controller, startup, autostart, and Guided setup.
- **Diagnostics** exposes target dry runs, current state, per-resource Health, providers, paths, and recent logs.

There are no separate Devices, Theme, Layouts, Cursor, DDC/CI, or Television pages. Those responsibilities live with their current feature owners above.

## Architecture at a glance

The managed solution is `app/PCMode.slnx`:

```text
PCMode.Tray -> PCMode.Engine -> resource providers
     |              |
     |              `-> Audio client/protocol -> PCMode.Audio.Host
     `-> PCMode.Picker                         -> Audio Engine/WASAPI/driver
```

`PCMode.Engine` owns desired state and provider orchestration. `PCMode.Tray` owns WPF composition and interaction. `PCMode.Picker` owns window-independent picker behavior. Native audio runs in a separate Host process behind a versioned named-pipe boundary; an x64 ACX/KMDF driver provides the PCMode virtual render endpoint and a single-consumer PCM transport.

An audio device alias switches the Windows default directly and works without Host or driver. An audio target starts one or more physical output routes in Host, then makes the virtual endpoint the Windows default so application PCM flows through PCMode's buffering, DSP, delay, resampling, synchronization, and recovery pipeline.

## Documentation map

- [System architecture](app/ARCHITECTURE.md): target composition, Engine/providers, subsystem boundaries, persistence, setup, and recovery.
- [Audio architecture](app/AUDIO_ARCHITECTURE.md): driver transport, Host/IPC, Audio Engine, WASAPI routes, synchronization, DSP, health, and Source activation.
- [Tray architecture](app/src/PCMode.Tray/ARCHITECTURE.md): WPF composition, navigation, page ownership/lifecycle, setup, tray, picker, hotkeys, and controller.
- [Development principles](app/DEVELOPMENT_PRINCIPLES.md): durable ownership, dependency, configuration, failure, concurrency, and testing rules.
- [Build and test guide](app/BUILD_AND_TEST.md): managed and driver prerequisites, commands, test ownership, and opt-in hardware tests.
- [Security policy](SECURITY.md): trust, privilege, IPC/driver, network, credential, and release boundaries.
- [Third-party notices](THIRD_PARTY_NOTICES.md): current dependency and platform inventory.

## Quick start

Managed development requires Windows and the .NET 10 SDK.

```powershell
dotnet restore app/PCMode.slnx
dotnet build app/PCMode.slnx --no-restore
dotnet test app/PCMode.slnx --no-build
dotnet run --project app/src/PCMode.Tray/PCMode.Tray.csproj
```

Warnings are treated as errors. The native audio driver is outside the managed solution and requires the matching Visual Studio/WDK/ACX/KMDF toolchain, elevation for installation, and an accepted driver signature. Follow [the build and test guide](app/BUILD_AND_TEST.md) before driver or hardware work.

The first run creates schema-version-1 configuration and offers Guided setup using the real Displays, Audio, Spaces, and Settings pages. The default picker hotkey is `Ctrl+Alt+Space`; configured targets may also have direct `Ctrl+Alt+letter` shortcuts. Global hotkey registration is all-or-nothing, so a conflict disables global input while leaving the GUI and tray usable.

## Configuration and local data

The default per-user root is:

```text
%LOCALAPPDATA%\PCMode\Native
```

`config.json` contains application settings, provider settings, and Spaces. Writes are atomic and retain `config.json.bak`; `state.json` records the last completed target; bounded logs live under `logs\`; LG pairing material is protected separately under `lgtv\` with current-user DPAPI. A failed configuration reload leaves the previous Engine active.

Use `--data-root <folder>` for isolated development. The worked configuration example is [app/config/pcmode.example.json](app/config/pcmode.example.json).

Useful runtime switches include `--background`, `--no-input`, `--no-controller`, `--picker-monitor primary|pointer|foreground`, and `--favorite Direction=Target[:Icon[:RRGGBB[:key]]]`. Unknown arguments are rejected.

## Troubleshooting

Start with Diagnostics and the bounded log under the active data root. Common boundaries are a delayed HDMI audio endpoint, unsupported DDC/CI through a dock/KVM/MST path, unavailable LG television/network state, a missing or incompatible virtual-audio driver/Host, and a saved configuration whose runtime reload is deferred by an active transition.

Hardware tests are opt-in and can change displays, default audio, volume, applications, monitors, or televisions. Do not enable them as a general test run; use the exact filtered commands and safety notes in [BUILD_AND_TEST.md](app/BUILD_AND_TEST.md).
