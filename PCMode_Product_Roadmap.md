# PCMode Product Roadmap

## Product vision

PCMode should become the dependable control surface for a Windows-based desk, gaming, simulation, creator, or television setup. A user chooses what they want to do, and PCMode converges the relevant computer, displays, audio, applications, monitors, lighting, and AV devices on one understandable state.

```text
Space -> Activity -> Source -> desired resource state
```

The product is a desired-state orchestrator, not a scripting engine or general home-automation platform. Its value is that a complete setup can be activated, observed, repaired, and left safely without the user understanding individual Windows APIs or device protocols.

## Current product baseline

PCMode already has the architectural foundation for v1:

- Space -> Activity -> Source configuration with inheritance, stable identities, and required/best-effort resources;
- one dependency-aware Engine path used by the GUI, tray, picker, hotkeys, controller, and startup behavior;
- graphical configuration for Spaces, displays, cursor placement, DDC/CI, LG televisions, audio, RGB, applications, and application settings;
- a merged Displays workflow and a guided setup wizard built from the real configuration pages;
- direct Windows audio-output switching as part of the normal Engine path, plus an optional native audio subsystem with a virtual endpoint driver, driver-to-user-mode PCM transport, persistent Audio Host, route Engine, physical WASAPI outputs, multi-route processing, buffering, resampling, synchronization, parametric EQ, Health, and recovery;
- diagnostics, bounded logs, configuration backup, automated tests, and explicitly gated hardware tests.

The remaining work is productization. v1 should complete, simplify, harden, package, and validate this product rather than add another broad layer of capability.

## Principles for roadmap decisions

- Keep one target model and one activation path.
- Prefer a smaller support promise that is reliable and well diagnosed.
- Do not add new pages, providers, hierarchy levels, DSP families, or remote services merely to make v1 appear larger.
- Keep kernel responsibilities small; routing, DSP, policy, synchronization, and recovery remain in user mode.
- Treat install, update, rollback, recovery, security, and uninstall as product features.
- Require ordinary workflows to work without JSON editing or command-line knowledge.
- Use field evidence and support demand to prioritize post-v1 expansion.

---

# Remaining work before paid v1

The workstreams below are ordered by product risk. Native Audio must be cleanly optional so its beta hardening and driver-signing work do not block the main paid v1 release.

## 1. Define and freeze the v1 support contract

Set the promises that implementation, onboarding, beta testing, and support must satisfy.

- Choose the supported Windows editions and builds for the main product. Define the optional Native Audio beta support matrix separately; the current virtual-audio INF targets Windows 11 x64, build 22000 or newer.
- Define the supported display, DDC/CI, LG television, controller, OpenRGB, and audio boundaries, including behavior when an optional integration is absent.
- Define and validate direct Windows playback-output switching as the supported v1 audio contract. Native Audio Targets, virtual-endpoint formats, DSP, synchronization, and multi-route behavior belong to the optional Native Audio beta support contract rather than the paid-v1 release gate.
- Decide the one-time-purchase, licensing, update-entitlement, and offline-use policy without making a cloud account necessary for normal use.
- Define the minimum beta evidence and support readiness required to charge for the product.

This workstream is complete when one concise support matrix and release policy can drive the installer, UI disclosures, tests, and product copy.

## 2. Finish and simplify the existing user workflows

Close gaps in the current product rather than introduce new capability families.

- Audit every page and normal Desk/TV workflow for confusing ownership, duplicate controls, incomplete validation, unsafe live actions, or any remaining need to edit JSON.
- Polish Guided setup so a new user can identify the important devices, understand optional integrations, configure direct Windows audio outputs without Native Audio, save a useful configuration, and perform a clearly explained first activation.
- Make activation outcome, degradation, and the next useful action visible from normal use—not only from raw logs. Retry and Repair should be reachable where a user encounters failure.
- Validate external-device Sources as ordinary resource compositions. Finish only the handoff and exit semantics required for predictable PC-to-HDMI and HDMI-to-PC workflows; bespoke console APIs remain out of scope.
- Validate the current picker, hotkeys, and controller flow from a couch. A controller user must be able to select and activate common targets and recover from a failed activation. A new large 10-foot UI is not required unless usability testing proves the existing picker inadequate.
- Freeze the initial schema and remove development-only compatibility or migration paths that are not part of the supported v1 contract.
- Remove dead UI, abandoned experiments, temporary diagnostics, generated artifacts, stale terminology, and transitional code before release.

This workstream is complete when representative Desk, TV, PC-source, and external-source workflows can be configured and used through the product UI with consistent behavior and no known blocking usability defect.

## 3. Complete recovery, diagnostics, and operational hardening

PCMode must remain recoverable when configuration or hardware is broken.

- Start into a useful recovery experience when `config.json` cannot be parsed or activated instead of terminating with only an error dialog.
- Add user-facing restore of `config.json.bak`, reset with a retained recovery copy, and clear explanations when a saved configuration could not become active.
- Review transition concurrency and define one consistent policy when a new request arrives while another transition is running: safely supersede at provider boundaries, queue only the latest request, or refuse visibly. Do not leave activation surfaces with inconsistent behavior.
- Ensure Health distinguishes desired, dispatched, observed, degraded, and failed state. Repair must preserve already-correct resources.
- Improve diagnostics for stale aliases, missing applications, unavailable displays/audio endpoints, DDC/CI limitations, unreachable televisions, startup-registration failures, and failures in optional integrations.
- Add a sanitized support bundle with versions, discovery summary, provider/target Health, recent bounded logs, redacted configuration, and optional-integration status. Prove through tests that credentials and sensitive values are excluded.
- Audit shutdown, sleep/resume, event subscriptions, background work, timeouts, cancellation, and resource disposal for hangs, duplicate work, or hidden state changes.

This workstream is complete when common configuration, provider, IPC, and hardware failures are understandable and recoverable without manually editing AppData, and the remaining security limitations are explicitly accepted rather than accidental.

## 4. Make Native Audio cleanly optional

Direct Windows audio-output switching remains part of the paid-v1 product. The existing Native Audio subsystem becomes an optional beta capability and must not block release of the rest of PCMode.

- Ensure direct physical audio-output targets work without the virtual endpoint, Audio Host, DriverClient, or native Audio Engine.
- Treat missing Native Audio as an unavailable optional capability, not as a global application or Health failure.
- Start or probe Native Audio components only when a Native Audio target is selected or its beta configuration is explicitly opened.
- Keep direct-output discovery, activation, verification, recovery, and diagnostics fully functional without Native Audio installed.
- Preserve the existing Native Audio architecture and configuration path when the optional beta is installed.
- Make transitions between direct outputs and Native Audio targets bounded and recoverable, including switching back to a direct output after Native Audio failure.

This workstream is complete when PCMode can be installed and used normally, including switching and verifying physical Windows playback outputs, with Native Audio entirely absent; Native Audio failures affect only targets that explicitly depend on it.

## 5. Build commercial release and update infrastructure

- Produce a canonical signed Windows release artifact and a straightforward installer.
- Install the main application with clear privilege prompts; create only the intended Start menu and startup entries. Native Audio beta installation is optional and is not required for the main product.
- Preserve user configuration during upgrade and offer an explicit data-retention choice during uninstall.
- Implement versioned update discovery, signed downloads, checksums, release notes, restart coordination, rollback, and recovery from an interrupted or failed update.
- Automate restore, build, managed tests, packaging, versioning, checksums, and release metadata in a reproducible release pipeline. Native-driver validation is required for Native Audio beta releases, not for the paid-v1 release gate.
- Sign application, installer, and update payloads as required by the main-product support contract.
- Integrate the chosen purchase/license policy with a graceful offline and recovery story. Avoid invasive DRM and do not make account availability a prerequisite for controlling the user's hardware.
- Audit the actual shipped files against third-party licenses and Microsoft runtime/driver redistribution terms.

This workstream is complete when clean install, upgrade, failed-upgrade recovery, rollback, and uninstall work on the supported Windows matrix and always leave either a working version or a documented recovery path.

## 6. Validate the product on real systems

Run a private beta broad enough to challenge the support contract rather than merely repeat the developer machine.

- Verify clean setup, normal use, recovery, update, and uninstall on the supported Windows builds and representative AMD, NVIDIA, and Intel systems.
- Cover single/multiple displays, laptop/dock and television arrangements, display disappearance, mixed refresh rates, supported LG televisions, and DDC-capable/unsupported paths.
- Cover onboard, USB, HDMI, soundbar/eARC, and changed-identity audio endpoints for direct Windows output switching, including default-endpoint contention and repeated switching.
- Validate that the main product remains healthy when Native Audio is absent or unavailable. Native Audio route, Host, driver, DSP, synchronization, and soak testing belong to the separate beta track.
- Verify picker, global shortcuts, controller reconnect, application launch/stop, OpenRGB present/absent, and external-source handoffs.
- Perform manual GUI checks for scaling, light/dark themes, keyboard focus, controller use, empty/loading/error states, and repeated navigation.
- Confirm a clean checkout can restore, build, test, package, and launch without untracked local dependencies.
- Triage every beta issue as release-blocking, disclosed v1 limitation, or post-v1 work.

This workstream is complete when no known crash, data-loss path, unsafe installer/update behavior, silent direct-audio switching regression, or repeatable core-transition failure remains, and supported limitations are disclosed before the user depends on them.

## 7. Paid-v1 release gate

Ship and charge for v1 only when:

- a new user can install, configure, activate, diagnose, recover, update, and uninstall without editing JSON;
- Space -> Activity -> Source and the supported Desk/TV/external-source workflows are understandable and stable;
- direct physical Windows audio outputs can be discovered, selected, activated, verified, and recovered without Native Audio installed;
- required and optional failures are visible, and Retry/Repair do not disturb correct state unnecessarily;
- configuration corruption and failed upgrades have tested recovery paths;
- application, installer, and updater artifacts are signed and version-compatible;
- the paid-v1 support matrix and representative hardware checks pass;
- security review, dependency/legal audit, product documentation, support process, pricing, and licensing decisions are complete.

The v1 product promise remains:

> **PCMode configures your entire setup around what you want to do.**

---

# Post-v1 development

Post-v1 priorities should be reordered using support volume, hardware prevalence, API reliability, maintenance cost, and commercial value. The optional Native Audio beta may continue in parallel before and after paid v1; promoting it to stable is not a paid-v1 release dependency.

## 1. Field-driven hardening and usability

Address real compatibility data first: provider timeouts, device identity changes, DDC/CI quirks, television firmware differences, direct-audio endpoint behavior, onboarding friction, accessibility, couch readability, and support diagnostics. Expand the supported matrix only when the product can detect and explain the new boundary.

## 2. Broader display and AV control

- HDR and explicit display-resolution resources with capability detection and readback;
- broader DDC/CI compatibility and carefully justified controls beyond brightness/input;
- capability-based support for additional television brands, CEC, receivers, soundbars, projectors, HDMI matrices, KVMs, and streaming devices;
- deeper device integrations only where their APIs are reliable enough for verification and recovery.

New providers must advertise only what they can support, remain bounded when hardware is absent, and participate in the normal Engine/Health model.

## 3. Native Audio beta and productionization

The existing Native Audio architecture continues as an optional beta subsystem until its driver, Host, realtime path, recovery, security, installation, update, and hardware-support boundaries are production-ready. It is not a paid-v1 release dependency.

- establish production driver signing, catalog generation, installation, update, disable, rollback, and uninstall through supported Windows mechanisms;
- package Audio Host, DriverClient-compatible components, and the driver as one versioned optional subsystem with an explicit compatibility policy;
- make partial or mismatched application/Host/driver updates fail visibly and recoverably;
- harden Audio Host IPC and the driver interface security boundary before promoting Native Audio from beta;
- verify Host adoption, long-lived playback across Tray restart, driver reconnect, endpoint/service restart, route recovery, and safe return to direct output;
- validate advertised formats, required/optional routes, live DSP changes, synchronization, and long-running playback across representative hardware;
- officially support more simultaneous routes when field evidence proves the synchronization and support burden manageable;
- richer missing-member and group-level policies;
- automatic latency measurement or microphone-assisted calibration;
- limiter, convolution, room correction, and more advanced profile management;
- carefully scoped audio-session inspection or controls;
- import/interoperability for valuable existing EQ workflows;
- separate privacy-explicit microphone/capture resources or pro-audio interfaces only in response to demonstrated demand.

Keep the virtual endpoint, DriverClient, Host, route Engine, and user-mode processing model. Do not promise arbitrary per-application routing, Bluetooth synchronization, exclusive-mode behavior, or DRM-protected playback without evidence that Windows can support the promise.

## 4. Local integrations and configuration portability

- a canonical CLI for target listing, activation, status, and Health;
- URI activation and authenticated local-only IPC for launchers and automation;
- Stream Deck and similar integrations through the same activation contract;
- configuration import/export that excludes credentials and prompts users to map logical aliases on different hardware;
- future schema migrations with backup, validation, rollback, and forward-version refusal.

Do not expose an unauthenticated LAN control surface or require cloud connectivity.

## 5. Additional Windows environment resources

Consider high-value desired-state resources such as power plan/mode, carefully scoped process priority, and application window layouts. They must read current state, converge through Engine, tolerate missing applications, and avoid turning PCMode into a generic Windows optimizer or full window manager.

## 6. Provider and contributor ecosystem

When contribution volume justifies it, stabilize and document provider contracts, publish a small sample provider and deterministic harness, and initially ship contributed providers in the main product. Dynamic third-party DLL loading, sandboxing, signing, compatibility negotiation, and a marketplace should wait for a proven need.

Provider breadth should follow user value, legality, protocol reliability, and long-term support cost. A limited Home Assistant scene bridge may be appropriate; replacing Home Assistant is not.

## 7. Distributed coordination only if a workflow requires it

Remote Windows agents, multiple PCs, a home-server coordinator, or additional frontends may eventually reuse the Space -> Activity -> Source and desired-state model. Do not add network architecture for conceptual purity. Any distributed design must preserve capability discovery, verification, recovery, explicit authentication, and useful local operation without a cloud account.

## Long-term product boundary

PCMode orchestrates resources that directly participate in computing, entertainment, productivity, simulation, creator, or AV activities. Displays, televisions, projectors, consoles, streaming devices, receivers, soundbars, audio interfaces, HDMI/KVM equipment, controllers, relevant lighting, and local software fit that boundary.

General household automation, enterprise fleet management, cloud-first accounts, unrelated IoT, a generic scripting language, and speculative platform architecture do not. PCMode may invoke a relevant external scene later rather than becoming the system that owns the whole home.
