# Security policy

## Supported versions

Security fixes are provided for the latest available PCMode release. Pre-release builds and older builds may be replaced by a newer build rather than patched separately.

## Trust model

PCMode is a local Windows desktop application for the signed-in user. The Tray, Engine, audio Host, configuration, logs, and device integrations normally run without elevation. PCMode is not a sandbox: a user who can edit its configuration can ask it to change machine state, launch configured executables or URI handlers, stop configured processes, send guarded input to a foreground application, and control configured local/network devices.

Treat `config.json` and any replacement data root as trusted user input. Do not copy an untrusted configuration into the profile and activate it. The default root inherits the signed-in user's profile permissions:

```text
%LOCALAPPDATA%\PCMode\Native
```

`--data-root` may point elsewhere; the confidentiality and integrity of that location are then determined by its filesystem ACLs. PCMode does not harden a custom root's permissions.

## Privilege boundaries

Normal operation uses current-user rights. Current-user operations include:

- configuration, state, backups, logs, and DPAPI-protected television credentials;
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` registration;
- display, Core Audio, DDC/CI, theme, notification, cursor, process, hotkey, controller, OpenRGB, and television operations available to that user;
- launching and connecting to `PCMode.Audio.Host`.

The virtual audio endpoint is different: it is a kernel-mode KMDF/ACX driver. Building it does not require elevation, but installing, updating, disabling, or creating its `ROOT\PCMODEAUDIO` device does. `Rebuild-PCModeAudioDriver.ps1 -Install` and `-Stop` explicitly require an elevated PowerShell and x64 DevCon.

Windows must accept the driver package's signature. The repository's driver script builds and installs the package but does not enable test-signing mode, change Secure Boot, install a certificate into trust stores, or provide production/attestation signing. Do not weaken Windows driver-signing policy on an ordinary machine merely to run PCMode. A production release must use an approved Windows driver-signing and installation process and must verify the exact INF, CAT, and SYS being installed.

## Local IPC and native driver boundaries

The desired-state Engine does not load the real-time audio graph in-process. `PCMode.Audio.Host` owns WASAPI streams and the driver connection and accepts versioned commands over the local `PCMode.Audio.Host` named pipe. Wire messages are size-bounded and protocol-version checked. A named semaphore prevents two Host processes from owning the same pipe claim.

The current pipe has no application-level authentication and the server does not set an explicit current-user ACL in code; it relies on Windows' default named-pipe access control. Any local principal that Windows permits to open the pipe can request status/Health, apply or recalibrate an audio target, stop audio, or shut down the Host. The pipe is not a network listener, but it is a same-machine control boundary and must be hardened before PCMode is treated as safe in a hostile multi-user local environment.

`PCMode.Audio.DriverClient` opens the driver's device interface with read access and sends versioned IOCTLs. The driver accepts one PCM consumer and validates protocol/capability/structure sizes, but the driver project does not assign an explicit device-interface SDDL in its source. Access therefore depends on the device security Windows/ACX supplies. Treat the device interface and every IOCTL handler as an untrusted kernel boundary: validate all lengths, state transitions, counters, handles, and arithmetic in the driver; never trust the managed client to have done so first.

The Host and driver should be versioned and shipped as one subsystem. Protocol-major mismatch, an older minor version, or missing required capabilities is refused rather than guessed around.

The non-security data flow and recovery design are documented in [Audio architecture](app/AUDIO_ARCHITECTURE.md).

## Network boundaries

PCMode has no telemetry client, updater, cloud account, or unauthenticated remote-control server in the current implementation. Network traffic occurs only for configured integrations:

- **LG webOS television:** WSS to the configured host on port 3001. webOS uses a self-signed certificate, so the first pairing connection accepts the presented certificate while the user confirms the prompt on the television. PCMode then pins that certificate's SHA-256 fingerprint and refuses later mismatches. Wake-on-LAN sends repeated UDP packets to the configured host plus applicable limited/subnet broadcasts on ports 7 and 9.
- **OpenRGB:** TCP to the OpenRGB SDK server at `127.0.0.1:6742`. PCMode does not start, install, or configure the OpenRGB server.

Television and OpenRGB responses are untrusted input. Message sizes, schemas, timeouts, device counts, names, and values must remain bounded and validated. Do not relax certificate pinning or enable a remote OpenRGB address without an explicit security review.

DDC/CI, Windows display/Core Audio APIs, XInput, and the virtual endpoint are local machine/device interfaces rather than network services, but their returned buffers and values are still untrusted native input.

## Credentials and sensitive data

LG client keys are not stored in `config.json`. `ProtectedLgTvCredentials` writes one file per television under `lgtv\`, encrypted with Windows DPAPI for the current user and additional entropy derived from the television host. The stored credential also contains the pinned certificate fingerprint. Cleartext buffers are zeroed after use where the implementation holds them directly.

Current-user DPAPI protects against another user or a copied profile file; it does not protect against code already running as the same user. Re-pair after moving a configuration to another machine or user profile.

Do not add secrets to configuration, state, logs, exception messages, UI status, diagnostics, tests, or sample files. There is no support-bundle exporter or central log-redaction pass: each boundary must ensure its own messages contain no secret before calling `LocalLog`.

## Configuration and process safety

- Schema/provider/hierarchy candidates are validated before they are activated.
- Configuration-store writes use a temporary file and atomic replacement, preserving one previous generation as `config.json.bak`.
- A failed runtime reload leaves the previous engine active.
- Stable logical aliases are separated from hardware identifiers; ambiguous endpoint names and conflicting provider keys are refused.
- Executable launches use `UseShellExecute=false` and `ArgumentList`, so arguments are not concatenated into a shell command. Configured URIs intentionally use the registered Windows shell handler.
- Synthetic keys are sent only after the configured process's window is confirmed to be foreground; otherwise the operation refuses to type.
- Global hotkeys are claimed as a set and fully rolled back if any registration fails.

These checks reduce accidental and injection-prone behavior; they do not make an untrusted configuration safe.

## Logs and privacy

`LocalLog` is best-effort, single-line, size-bounded logging. By default it keeps one 512 KiB current file and two archives. Logs may contain target names, device names/IDs, process names, file paths, configured network hosts, provider status, and exception text. They are local but not encrypted.

Inspect and redact logs before sharing them. Pairing keys should never appear, but hostnames, installed paths, and hardware identities may still be sensitive. Deleting the log folder removes the current local diagnostic history; PCMode recreates it as needed.

## Development and hardware tests

Ordinary automated tests use fakes and temporary roots. Hardware tests are opt-in through explicit environment variables because they may:

- change the Windows default audio endpoint and endpoint volumes;
- play audible tones or hold the virtual endpoint/driver transport;
- change and restore monitor brightness;
- apply real target resources, launch applications, switch displays, or control a television.

Run hardware tests only with a narrow test filter on a machine you control. Stop the production Tray/Host when instructed, keep the display and audio recoverable, and do not assume cleanup can reverse a process crash or power loss.

Exact opt-in variables and filtered commands are maintained in the [build and test guide](app/BUILD_AND_TEST.md).

## Release security requirements

Before a release:

1. Audit the actual published Tray, Host, runtime, native driver, and installer contents.
2. Verify Authenticode and driver/catalog signatures and their trust chains.
3. Verify package/license notices and dependency versions.
4. Test clean install, update, rollback, uninstall, and recovery on supported Windows builds.
5. Review named-pipe and device-interface ACLs, local multi-user behavior, and protocol fuzz/error handling.
6. Confirm credentials and sensitive configuration do not enter logs or artifacts.
7. Re-run opt-in hardware verification on representative displays, DDC/CI paths, audio endpoints, and television firmware.

## Reporting a vulnerability

Do not publish credentials, exploit details, private logs, or other sensitive material in a public issue.

If private vulnerability reporting is enabled for the repository, use its Security page. Otherwise, open a public issue containing only a request for a private reporting channel and enough non-sensitive context for the maintainer to respond.

A useful private report includes the PCMode and Windows versions, affected boundary/provider, reproduction steps or proof of concept, potential impact, whether sensitive data was exposed, and any proposed mitigation.

There is currently no bug-bounty program or guaranteed response SLA.
