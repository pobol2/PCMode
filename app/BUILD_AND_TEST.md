# Build and test guide

This document separates ordinary managed development from native-driver and real-hardware work. Commands run from the repository root unless noted.

## Managed prerequisites

- Windows;
- .NET 10 SDK.

Restore, build, and test the managed solution:

```powershell
dotnet restore app/PCMode.slnx
dotnet build app/PCMode.slnx --no-restore
dotnet test app/PCMode.slnx --no-build
```

`app/Directory.Build.props` treats managed warnings as errors. The native virtual-endpoint driver is not part of `PCMode.slnx`.

Run the Tray during development:

```powershell
dotnet run --project app/src/PCMode.Tray/PCMode.Tray.csproj
```

The Tray build also builds `PCMode.Audio.Host` and copies its complete output into `AudioHost/` under the Tray output. A Host already running from that folder can lock those binaries and prevent the final copy. Shut down that development Host deliberately before rebuilding its deployed output; do not kill an active Host without first moving Windows away from the PCMode virtual endpoint if preserving playback matters.

## Runtime options

```text
--background
--no-input
--no-controller
--data-root <folder>
--picker-monitor primary|pointer|foreground
--favorite Direction=Target[:Icon[:RRGGBB[:key]]]
```

Unknown or malformed arguments are rejected. `--data-root` is the preferred way to isolate manual configuration tests from the normal `%LOCALAPPDATA%\PCMode\Native` root.

## Managed test structure

| Project | Scope |
|---|---|
| `PCMode.Engine.Tests` | Configuration, hierarchy, target catalog, planning, providers, convergence, persistence, migration, and Engine-level hardware gates. |
| `PCMode.Picker.Tests` | Geometry, easing, selection, favorites, and controller translation without windowing. |
| `PCMode.Tray.Tests` | ViewModels, navigation/catalog parity, page construction/lifecycle, configuration shells, setup, and feature ownership. |
| `PCMode.Audio.Core.Tests` | Models, buffers, processing chains, gain, EQ, and response calculations. |
| `PCMode.Audio.Engine.Tests` | Graph, routes, buffering, timing, resampling, synchronization, health, live calibration, and recovery. |
| `PCMode.Audio.Windows.Tests` | Wave-format and Windows audio boundary behavior that can be exercised without changing endpoints. |
| `PCMode.Audio.DriverClient.Tests` | Driver protocol layout/validation, connection behavior, packet conversion, staging, and reconnect logic. |
| `PCMode.Audio.Protocol.Tests` | Host wire framing and compatibility. |
| `PCMode.Audio.Integration.Tests` | Host runtime, named-pipe server/client, ownership-mode parsing, commands, and recovery with fake engines. |
| `PCMode.Audio.HardwareTests` | Explicitly enabled real driver, endpoint, Host, and physical-output checks. |

Ordinary tests use fake providers, fake audio streams, or temporary roots and do not intentionally change normal machine state. Hardware test classes are present in the solution but skip unless their exact opt-in variable is set.

WPF tests that construct pages or windows run on an STA thread. Tests involving process-wide WPF pack-resource initialization join the serialized WPF collection; unrelated tests remain parallel.

Run a focused project or test while iterating:

```powershell
dotnet test app/tests/PCMode.Audio.Engine.Tests/PCMode.Audio.Engine.Tests.csproj
dotnet test app/tests/PCMode.Engine.Tests/PCMode.Engine.Tests.csproj --filter "FullyQualifiedName~TargetCatalog"
```

## Native driver prerequisites and build

The driver is at `app/driver/PCMode.Audio.VirtualEndpoint` and currently requires:

- x64 Visual Studio/MSBuild with C++ driver support;
- Windows SDK/WDK `10.0.28000.0`;
- ACX 1.1 and KMDF 1.33;
- x64 DevCon for install/update/disable operations;
- Windows 11 x64 build 22000 or newer for the INF target;
- a driver/catalog signature accepted by the machine.

Build and validate the Debug x64 package:

```powershell
.\Rebuild-PCModeAudioDriver.ps1
```

Script switches:

| Switch | Effect |
|---|---|
| `-Install` | Install or update `ROOT\PCMODEAUDIO` after the build. Requires an elevated PowerShell. |
| `-TestManaged` | Build and test `PCMode.Audio.DriverClient` after native work. |
| `-DontStopProcesses` | Leave running PCMode/Host processes alone; locked files or live driver handles may then block work. |
| `-Stop` | Disable/unload the installed PCMode driver and exit without building. Requires elevation. |
| `-Root <path>` | Override the repository root assumed by the script. |

Unless `-DontStopProcesses` is supplied, the script may forcibly stop PCMode and Audio Host processes. Stop-only work may also stop and restart Windows audio services while disabling the driver. Read the script output and do not run it during audio or other work that must remain uninterrupted.

The script does not establish certificate trust, enable Windows test-signing, change Secure Boot, or perform production signing. Driver installation and release requirements are security-sensitive; see [SECURITY.md](../SECURITY.md).

## Opt-in hardware tests

Hardware tests can change default audio, play sound, change monitor brightness, start applications, switch display/television state, or restart audio components. Run only a fully qualified hardware test filter after reading that test's comments. Restoration is best effort.

### Audio subsystem hardware tests

Set `PCMODE_AUDIO_HW=1` to enable `PCMode.Audio.HardwareTests`. Individual tests use the variables appropriate to their scenario:

- `PCMODE_AUDIO_SOURCE_ID` for a WASAPI loopback development source;
- `PCMODE_AUDIO_OUTPUT_ID` for one physical playback endpoint;
- `PCMODE_AUDIO_OUTPUT_A_ID` and `PCMODE_AUDIO_OUTPUT_B_ID` for two-route checks;
- `PCMODE_AUDIO_OUTPUT_MISMATCH_ID` for format-conversion coverage;
- `PCMODE_AUDIO_HOST_PATH` for the Host executable.

Example shape:

```powershell
$env:PCMODE_AUDIO_HW = "1"
$env:PCMODE_AUDIO_OUTPUT_ID = "<endpoint-id>"
$env:PCMODE_AUDIO_HOST_PATH = "<path-to-PCMode.Audio.Host.exe>"
dotnet test app/tests/PCMode.Audio.HardwareTests/PCMode.Audio.HardwareTests.csproj --filter "FullyQualifiedName~VirtualEndpointHardwareTests"
```

These tests include playback, multi-output synchronization, endpoint disconnect/reconnect, driver/Host restart, live EQ changes, format mismatch, and a long synchronization soak. Select only the intended test; do not enable the whole project casually.

### Engine audio-target hardware test

`AudioTargetHardwareTest` exercises the normal `AudioProvider` path using a copied real configuration, a real configured audio target, the virtual endpoint, and Host:

```powershell
$env:PCMODE_AUDIO_HARDWARE_TEST = "1"
dotnet test app/tests/PCMode.Engine.Tests/PCMode.Engine.Tests.csproj --filter "FullyQualifiedName~AudioTargetHardwareTest"
```

Optional overrides are `PCMODE_AUDIO_HARDWARE_ROOT`, `PCMODE_AUDIO_HARDWARE_TARGET`, `PCMODE_AUDIO_HARDWARE_SECONDS`, and `PCMODE_AUDIO_HOST_PATH`.

### Hierarchy hardware tests

`PCMODE_HIERARCHY_HARDWARE_TEST=1` enables real-provider composition against a copied data root. Without `PCMODE_HIERARCHY_TARGET`, the general check remains a dry run. Supplying a target key/path/leaf permits a real apply.

The external-source handoff test additionally requires `PCMODE_HIERARCHY_PC_TARGET` and `PCMODE_HIERARCHY_EXTERNAL_TARGET`. `PCMODE_HIERARCHY_HARDWARE_ROOT` overrides the source data root.

### DDC/CI hardware test

`PCMODE_DDC_HARDWARE_TEST=1` enables `DdcHardwareTest`. It reads, changes, and attempts to restore brightness on a configured real monitor; input state is read but not changed. DDC/CI behavior depends on the monitor, cable, dock, KVM/MST path, GPU driver, and firmware.

## Before submitting a change

1. Run the focused owning tests while iterating, then the full managed suite when practical.
2. Keep ordinary tests independent of installed hardware and the user's normal data root.
3. Run hardware tests only when the change crosses that boundary and record the exact scenario used.
4. For driver changes, verify the native package, protocol compatibility, installed binary identity, and managed DriverClient tests.
5. Update architecture, security, configuration examples, and dependency notices when their current contracts change.
