# PCMode Product Roadmap

## Purpose

This is the implementation order for taking PCMode from its current native Windows foundation to a paid product. It is intentionally detailed enough to guide implementation without becoming a complete product-design specification.

Work through the numbered steps in order. A step is complete only when its acceptance criteria pass. Implementation from a later step may be prepared early when it reduces rework, but later scope must not delay the active step or silently expand paid v1.

## Version framing

PCMode is pre-release. This roadmap defines the first product version, **PCMode v1**. The current repository is a development foundation, not a previously released product.

Describe the completed architecture, configuration, GUI, and documentation as the initial v1 design, not as replacements for an older product version. Temporary conversion of development data may be used while building v1, but unnecessary compatibility paths must not remain in the completed v1 foundation.

## Product direction

PCMode configures a user's setup around what they want to do:

```text
Space → Activity → Source
```

- **Space** is where the setup is used, such as Desk, TV, or Sim Rig.
- **Activity** is what the user is doing, such as Work, Gaming, or Movies.
- **Source** provides the experience, such as Steam, Plex, or a Nintendo Switch.

The selected intent produces one desired state:

```text
Space state + Activity state + Source state → final desired resource state
```

PCMode then plans, applies, verifies, and repairs that state. It remains a desired-state orchestrator, not a generic scripting engine or home-automation platform.

## Starting foundation

Build forward from the native Windows application and its existing resource/provider, planning, dependency, and convergence engine. Do not replace this engine when introducing the new product model.

Party mode is already working and is part of the starting foundation. Preserve its proven behavior: normal shared-mode Windows audio is mirrored to the configured speakers and soundbar with a selectable reference output, adjustable delay, independent levels, drift control, deterministic ownership and shutdown, visible failures, and no silent device substitution.

---

# Required before paid v1

## Step 1 — Finish essential graphical configuration

### Outcome

A normal user can configure every planned v1 workflow without opening or editing JSON.

### Required work

- Expose logical aliases for displays, audio devices, applications, and TV inputs.
- Configure display enablement, primary display, placement, layout, and refresh rate.
- Configure normal audio outputs and the existing Party mode, including endpoint selection and live delay/level calibration.
- Configure application executable or URI launch, process detection, readiness, restart, focus/reactivation, and stop behavior.
- Configure supported LG TV discovery/addressing, pairing, power, input aliases, profiles, and currently supported advanced settings.
- Expose existing cursor, theme, Do Not Disturb, OpenRGB, required/optional resource, dependency, naming, and ordering settings.
- Validate edits before saving and show field-specific errors.
- Round-trip existing configuration without dropping unknown or advanced values.
- Show friendly names by default and reserve raw provider keys and hardware IDs for advanced details and diagnostics.

### Acceptance criteria

- A new user can build a working Desk and TV configuration entirely in the GUI.
- An existing configuration can be opened, edited, saved, and reloaded without loss.
- Invalid edits cannot corrupt or replace the last valid active configuration.
- Party mode can be configured and calibrated without editing files manually.
- Intentionally unsupported provider fields are clearly identified rather than appearing as unexplained read-only values.

### Deferred

- New explicit display-resolution selection and verification.
- Generalized Audio Target/group configuration and Equalizer APO profile selection; those arrive in Step 7. A full EQ editor remains post-v1.

## Step 2 — Make reliability visible and actionable

### Outcome

Users can see whether the requested state was reached and can recover from partial failure without reading logs.

### Required work

- Show overall target health and per-resource states: correct, pending, degraded, failed, unavailable, or unknown.
- Show transition progress using meaningful resource descriptions.
- Explain partial failures in plain language and retain useful native/provider details behind a Details view.
- Add targeted Retry for the failed operation or resource.
- Add Repair that re-reads current state and reapplies only incorrect resources.
- Allow a newer activation request to supersede an older transition at safe cancellation boundaries.
- Prevent cancellation from interrupting an indivisible hardware command halfway through.
- Include Party endpoint, capture/render, delay, clock-offset, drift-correction, buffer, and last-failure state in Health.

### Acceptance criteria

- A failed resource is visible in the main UI with a useful next action.
- Repair preserves resources that already match desired state.
- A second activation safely converges toward the newest request without continuing obsolete work.
- Partial success remains visible and is not reported as total success or an unexplained failure.
- Automated tests cover success, partial failure, retry, repair, timeout, and superseding transitions.

### Deferred

- Remote monitoring or recovery.
- Generic user-authored recovery scripts.

## Step 3 — Simplify GUI architecture and composition

### Outcome

GUI structure is predictable and localized. Pages, navigation, and layouts can be added, moved, split, or reorganized without tracing unrelated startup, engine, provider, or configuration code.

### Required work

- Keep application startup focused on application-wide initialization, lifetime, and shutdown.
- Move page and view-model creation out of startup code into a clear GUI composition structure.
- Define navigation pages, ordering, labels, and metadata in one obvious registry.
- Give each major GUI area ownership of its View, ViewModel, and presentation logic.
- Standardize how pages access configuration, save changes, refresh state, perform discovery, activate targets, and surface errors.
- Replace unnecessary raw paths, cross-page callbacks, and special-case wiring with small capability-focused services.
- Avoid creating a general GUI service, service locator, or container passed to every view model.
- Centralize reusable GUI controls and styles for navigation, page headers, forms, validation, loading, empty, and error states.
- Add a short GUI architecture map identifying:
  - the navigation registry;
  - page composition entry point;
  - shared UI services;
  - common style resources;
  - expected folder structure for each GUI area.
- Keep UI composition separate from the desired-state engine and provider implementations.
- Preserve the project boundaries:
  - Tray may depend on Engine and Picker;
  - Engine and Picker remain independent of Tray and each other.
- Preserve existing native-operation interfaces.
- Remove clear duplication, obsolete GUI plumbing, and dead code exposed by the refactor.
- Avoid dependency-injection frameworks or additional abstraction layers unless they provide a concrete simplification.

### Acceptance criteria

- Adding, removing, renaming, or rearranging a page requires changing one clear navigation definition.
- Regrouping GUI sections does not require changes to application startup, Engine, or providers.
- Adding a page requires only its GUI area, one navigation registration, and relevant tests.
- Moving controls between pages or splitting a large page primarily affects the involved GUI areas.
- A normal GUI change requires inspecting only the relevant View/ViewModel and a small, documented set of shared GUI files.
- View models can be unit-tested with fake services.
- Pages can be smoke-tested without real hardware.
- Changing the main window layout does not require Engine or provider changes.
- Native operations remain isolated behind approved interfaces.
- `App.xaml.cs` no longer contains page-specific construction and navigation details.
- Existing behavior and automated tests continue to pass.

### Deferred

- Do not implement Space → Activity → Source in this step.
- Do not redesign every existing screen simply because the architecture allows it.
- Do not rewrite the desired-state engine or provider architecture.
- Do not introduce a dependency-injection framework without a concrete requirement.
- Do not add product features during the cleanup.

## Step 4 — Introduce Space → Activity → Source

### Outcome

The user-facing model expresses intent as Space → Activity → Source while continuing to use the existing desired-state engine and providers.

### Required work

- Add persistent Space, Activity, and Source identities with stable logical IDs and editable names.
- Associate Activities with a Space and Sources with an Activity.
- Support Activities that do not require a Source.
- Store common state once at the Space or Activity level instead of duplicating it across Sources.
- Define deterministic composition:
  - Activity overrides Space;
  - Source overrides Activity;
  - an omitted value inherits;
  - an explicit clear, disable, or Off value overrides inherited state.
- Represent PC applications, external HDMI devices, and smart-TV applications as Source types without assuming every Source is a Windows process.
- Keep actual external-device handoff behavior deferred to the later external-Sources step.
- Compose the hierarchy into one engine-compatible desired state rather than creating a second execution path.
- Add GUI operations for creating, editing, deleting, ordering, and activating Spaces, Activities, and Sources.
- Update current-target, transition progress, Health, Repair, tray, and picker state to identify the selected hierarchy.
- Define Space → Activity → Source as the initial shipping configuration schema, identified as schema v1.
- Provide a one-time conversion for current local flat-mode development configurations.
- Back up the development configuration, validate the converted result, and make conversion repeatable without creating duplicates or losing data.
- Keep provider-specific settings below the intent model.

### Acceptance criteria

- Current local development configurations can be converted without losing resource settings.
- Repeating the development conversion produces the same result without duplicate objects.
- The source development configuration remains recoverable.
- The completed product uses Space → Activity → Source schema v1 without a competing flat-mode path.
- Common Space and Activity settings are stored once and reused.
- Inherited, overridden, explicitly disabled, and omitted values compose predictably.
- Activities work both with and without Sources.
- Users can manage and activate the hierarchy through the GUI.
- Switching Sources preserves already-correct Space and Activity resources.
- External Sources can be represented without fake application data.
- The existing engine and providers receive one final composed desired state.
- Progress, Health, Repair, tray, and picker state identify the active Space, Activity, and Source where applicable.
- Invalid identities, relationships, and composition conflicts are rejected before activation.
- Composition and the temporary development-data conversion have deterministic automated tests.

### Deferred

- Arbitrary additional hierarchy levels.
- External HDMI handoff and device-control behavior.
- Preferred Sources and first-class controller presentation.
- Guided templates and onboarding.
- Database-backed configuration.
- Remote or multi-PC ownership of a Space.

## Step 5 — Complete foundation integration

### Outcome

The Space → Activity → Source model is fully connected to the existing product and ready for the Step 6 checkpoint. This step adds no new product capabilities.

### Required work

- Connect Space → Activity → Source consistently across the GUI, tray, picker, startup behavior, progress reporting, Health, Repair, and recorded current state.
- Ensure existing display, audio-device, application, LG TV, DND, RGB, theme, and cursor behavior uses the new desired-state and activation path.
- Verify representative Desk and TV configurations can be created, edited, activated, and switched.
- Verify Activity and Source changes within the same Space.
- Ensure unchanged resources are not unnecessarily disrupted during transitions.
- Ensure missing optional hardware or integrations do not block unrelated resources.
- Finish the temporary development-data conversion and remove obsolete flat-mode execution and UI paths.
- Add deterministic integration tests using fake hardware boundaries.
- Fix any known incomplete integration that would prevent a meaningful Step 6 audit.

### Acceptance criteria

- Space → Activity → Source is the only active product model.
- Existing capabilities work through one desired-state composition and activation path.
- GUI, tray, picker, progress, Health, Repair, and recorded current state agree on the selected target.
- Representative Desk and TV workflows work in automated integration tests.
- Missing optional integrations fail clearly without blocking unrelated resources.
- No known incomplete integration prevents starting the Step 6 checkpoint.

### Deferred

- GUI redesign and visual customization beyond what is required for integration.
- Broad manual and hardware verification; handled in Step 6.
- Generalized audio and Equalizer APO profiles.
- HDR.
- Explicit display-resolution control and DDC/CI.
- External-source integrations and other new product features.

## Step 6 — Foundation consolidation and internal checkpoint

### Outcome

Steps 1–5 form a clean, reliable, tested foundation. The architecture, GUI, configuration, documentation, and supported hardware workflows are verified before generalized audio and later product expansion begin.

This is a temporary feature freeze and an internal development checkpoint on the path to the first paid v1 release.

### 6.1 — Audit Steps 1–5

#### Claude Code

- Review every requirement and acceptance criterion from Steps 1–5.
- Map each requirement to its implementation, automated tests, and any required manual verification.
- Identify regressions, incomplete integrations, duplicated paths, dead UI, obsolete terminology, and weakly tested behavior.
- Produce an ordered list of required fixes.
- Do not modify code during the initial audit.

#### You

- Review the findings.
- Confirm which hardware and workflows are available for manual testing.
- Identify known limitations that may remain after this checkpoint.

#### Done when

- Every Steps 1–5 requirement is classified as complete, incomplete, broken, or requiring manual verification.
- There is one prioritized consolidation plan.

### 6.2 — Clean and stabilize the foundation

#### Claude Code

- Fix the architectural, configuration, development-data conversion, reliability, and integration problems found during the audit.
- Confirm that Engine, Picker, Tray, GUI composition, providers, and native-operation boundaries remain clear.
- Keep one desired-state composition and activation path.
- Remove obsolete flat-mode code after the temporary development-data conversion is no longer needed.
- Remove dead code, duplicated UI paths, obsolete terminology, temporary adapters, and unnecessary transitional plumbing.
- Check event subscriptions, background work, cancellation, disposal, and shutdown behavior for leaks or duplicate work.
- Add focused regression tests for every important fix.
- Avoid redesigning the GUI during this substep.

#### You

- Approve any change that materially affects the initial schema, development-data conversion, or supported workflows.

#### Done when

- The architecture is understandable without tracing unrelated startup or provider code.
- Configuration writes and development-data conversion remain validated, atomic, and recoverable.
- No known obsolete execution or configuration path competes with the current design.
- Required v1 behavior remains intact unless an intentional correction is documented.

### 6.3 — Reinforce the complete automated test suite

#### Claude Code

- Review the test architecture before expanding it. Test projects, folders, namespaces, and ownership should follow the production boundaries for Engine, Picker, Tray, providers, configuration, and GUI areas.
- Make it obvious where a new unit, integration, structural, GUI, manual, or hardware test belongs and how each category is run.
- Keep shared fakes, fixtures, builders, and test utilities small and purpose-specific. Remove confusing duplication, but do not create a large test framework or shared fixture that couples unrelated areas.
- Audit automated coverage across the entire current project and every existing v1 capability.
- Prioritize important success, failure, recovery, and edge-case behavior, including configuration validation and atomic writes, desired-state composition, activation, Health, Retry, Repair, cancellation and superseding, navigation, state synchronization, provider contracts, and error handling.
- Identify and correct missing, weak, duplicated, flaky, misleading, or unjustifiably skipped tests.
- Add focused unit, integration, structural, GUI-composition, and regression tests where they provide meaningful protection.
- Keep normal tests isolated and deterministic. Use fake providers, fake time, temporary data roots, and approved native seams so they never manipulate the development machine's real hardware or persistent configuration.
- Keep interactive-GUI, manual, soak, and physical-hardware tests clearly separated and explicitly gated.
- Run the complete normal automated suite and fix any testable product regressions revealed by the work.
- Leave a concise test map describing the suite structure, shared test infrastructure, normal commands, and gated-test categories.
- Produce a separate checklist of behavior that can only be verified manually or with physical hardware.

#### You

- Review the manual and hardware-testing checklist.
- Confirm that it covers the devices and workflows you intend to verify during Steps 6.5 and 6.6.

#### Done when

- All normal automated tests pass reliably.
- The test structure follows production boundaries and a developer can quickly determine where a new test belongs.
- Shared test infrastructure reduces duplication without hiding behavior or coupling unrelated areas.
- Every current project and major existing capability has meaningful automated protection at the appropriate level.
- Important success, failure, recovery, and edge-case behavior is covered.
- Normal tests are deterministic, isolated, and safe to run without real hardware or user configuration.
- Skipped or gated tests have a clear documented reason and execution method.
- Manual and hardware-only verification is explicitly separated from automated coverage.
- The test map accurately explains how the suite is organized and run.
- No arbitrary coverage percentage is required.

### 6.4 — Customize and polish the GUI

#### You

- Decide the preferred navigation order, page grouping, terminology, control placement, and general layout.
- Give Claude Code one bounded GUI area or change set at a time.
- Prioritize usability problems over purely decorative changes.

#### Claude Code

- Implement the approved GUI changes through the Step 3 composition structure.
- Keep navigation metadata centralized.
- Keep changes localized to the affected views, view models, and shared GUI resources.
- Reuse shared controls, styles, validation, loading, empty, and error states.
- Preserve keyboard, focus, accessibility, scaling, and theme behavior.
- Avoid changing the engine or providers solely to rearrange the GUI.
- Add or update focused GUI tests after each bounded change set.

#### Done when

- The main layout and navigation match the preferred design.
- Pages can still be moved, split, renamed, and reorganized without unrelated engine or provider changes.
- Every page and dialog opens correctly.
- Common states and controls remain visually consistent.
- The GUI works at the supported window sizes, themes, and display scaling levels.

### 6.5 — Perform manual GUI verification and fixes

#### You

Manually verify:

- Navigation, page ordering, labels, and selection state.
- Every page, dialog, picker, tray action, and settings workflow.
- Save, discard, validation, loading, empty, success, warning, and error states.
- Light and dark themes.
- Window resizing and common display-scaling levels.
- Keyboard navigation, focus order, controller-oriented flows, and recovery from errors.
- Repeated navigation and reopening pages for duplicated handlers or stale state.

Report problems with reproduction steps, screenshots when useful, and relevant logs.

#### Claude Code

For each confirmed problem:

- Diagnose the underlying cause.
- Fix the problem in the owning GUI area.
- Remove duplicated or obsolete UI exposed by the fix.
- Add a regression test when practical.
- Re-run the relevant automated tests.

#### Done when

- No known GUI defect blocks normal use.
- There are no known duplicate navigation paths, dead screens, or misleading controls.
- Remaining cosmetic limitations are documented and intentionally deferred.

### 6.6 — Perform workflow and hardware verification

#### You

Test the available real workflows and hardware, including:

- A clean schema-v1 configuration and one converted from the current development format.
- Display discovery and activation.
- Existing audio-device behavior.
- Party mode.
- LG TV control when applicable.
- Picker, tray, shortcuts, startup behavior, and controller/couch operation.
- Desk and TV Spaces, Activities, and Sources.
- Supported application launch and stop behavior.
- Missing, disconnected, renamed, or unavailable hardware.
- Restarting the application and reloading configuration after changes.
- Recovery and Repair after a partial or failed activation.

#### Claude Code

For each confirmed problem:

- Determine whether it is a product defect, integration issue, hardware limitation, or documentation issue.
- Fix product defects and add fake-provider regression tests where possible.
- Improve diagnostics when a hardware-specific failure cannot be reproduced automatically.
- Keep the verification checklist and known limitations current.

#### Done when

- The primary workflows pass on the hardware available for this checkpoint.
- Failure behavior is understandable and recoverable.
- Known hardware limitations are documented.
- Broad hardware compatibility testing remains assigned to the later hardware-beta step.

### 6.7 — Run the final regression and re-audit

#### Claude Code

- Run the complete automated test suite after all GUI and hardware fixes.
- Recheck every Steps 1–5 acceptance criterion.
- Search for obsolete terminology, dead code, duplicate implementations, skipped tests, temporary workarounds, and unresolved checkpoint items.
- Confirm that fixes made during manual testing did not introduce new architectural shortcuts.
- Produce a final checkpoint report containing passed checks, manual results, known limitations, and deferred work.

#### You

- Review the final report.
- Decide whether each remaining limitation is acceptable for the internal checkpoint.

#### Done when

- Every Steps 1–5 acceptance criterion has evidence of completion.
- All normal automated tests pass.
- No known blocking regression remains.
- Remaining limitations are explicit and accepted.

### 6.8 — Clean the repository and update documentation

#### Claude Code

- Remove temporary diagnostics, abandoned experiments, stale comments, and obsolete documentation.
- Confirm generated files, local configuration, logs, secrets, and build output are excluded from version control.
- Update the stable documentation to describe:
  - The project architecture and dependency boundaries.
  - Space → Activity → Source composition.
  - The initial configuration schema, storage, validation, backup, and recovery.
  - How to build, test, run, and troubleshoot the application.
  - Supported workflows and known limitations.
  - Manual and hardware verification procedures.
- Keep roadmap-specific implementation detail in the roadmap rather than duplicating it throughout stable documentation.
- Verify proprietary licensing, security-reporting, and third-party-notice files remain appropriate.

#### You

- Read the documentation as if returning to the project after several months.
- Confirm it is sufficient to understand, build, test, and modify the project.

#### Done when

- Documentation matches actual behavior and architecture.
- The repository contains no obvious temporary, generated, duplicated, or abandoned material.
- A new development session can understand the foundation without reconstructing it from code history.

### 6.9 — Verify a clean checkout

#### Claude Code

- Verify dependency restoration, build, and automated tests from a clean checkout or equivalent clean environment.
- Confirm the application does not depend on untracked local files.
- Confirm example configuration and documented setup instructions are valid.
- Prepare the final internal-checkpoint summary.

#### You

- Launch the clean build.
- Perform a short smoke test of the most important GUI and activation workflows.

#### Done when

- The project can be restored, built, tested, launched, and configured from its documented repository contents.
- The clean build passes the final smoke test.

### 6.10 — Create the internal Git checkpoint

#### You

- Review the final changes and checkpoint report.
- Commit the completed foundation.
- Create an internal tag such as `v1-foundation`.
- Record any accepted limitations that should be addressed in later roadmap steps.

#### Done when

- The repository has a known-good, reproducible internal checkpoint.
- Later generalized-audio and expansion work can be compared against or rolled back to this foundation.

### Final acceptance criteria

- Every Steps 1–5 requirement has implementation and verification evidence.
- Architecture and GUI composition remain clear, localized, and scalable.
- The initial configuration schema and temporary development-data conversion are validated, recoverable, and tested.
- Automated tests pass and cover the major regression risks.
- The GUI has been customized and manually verified.
- Primary workflows have been tested on the available hardware.
- Dead code, duplicated UI, obsolete terminology, and temporary plumbing have been removed.
- Documentation and repository metadata match the current proprietary project.
- A clean checkout builds, tests, launches, and passes a smoke test.
- An internal Git checkpoint has been created.

### Deferred

- Generalized audio routing and EQ.
- New external-source integrations.
- Explicit display resolution control and DDC/CI.
- Release packaging, signing, updating, licensing, and paid-release infrastructure.
- Broad hardware beta coverage beyond the devices available for this checkpoint.
- Unrelated product features or speculative architecture.

## Step 7 — Generalize audio and add Equalizer APO profiles

### Outcome

Audio becomes a reusable desired-state resource built from the proven Party mode, with reliable single outputs, multiple named two-output groups, and optional processing profiles.

### Audio target model

Represent audio intent as a named target:

```text
Audio Target
├── Single output
└── Synchronized two-output group
```

Spaces, Activities, and Sources select an Audio Target. Applying a single output stops any active group runtime. Applying a group establishes its reference output before starting its mirror. Repeated activation must remain idempotent.

### Endpoint discovery and control

- Use stable endpoint IDs as identity and friendly names only for presentation.
- Convert any friendly-name mappings from the development foundation conservatively and require confirmation when remapping uncertain matches.
- Distinguish active, disabled, unplugged, not-present, stale mapping, no default, and unavailable audio-service states.
- Read Console, Multimedia, and Communications defaults separately.
- Make role policy explicit; preserve Communications unless the user elects to move it.
- Read back and verify every default role changed by PCMode.
- Process endpoint/default/property notifications asynchronously and retain bounded polling as a recovery mechanism.
- Handle HDMI endpoint arrival/disappearance with a quiet period before convergence.
- Support optional endpoint volume and mute targets; omission means leave the current value unchanged.
- Build one coherent topology snapshot rather than racing independent enumerations.
- Keep undocumented Windows default-device writes isolated behind a compatibility boundary and fail clearly when unavailable.
- Never automatically enable a disabled endpoint or silently select a similarly named replacement.

### Named synchronized groups

- Allow multiple named group configurations while keeping each paid-v1 group limited to exactly two render endpoints.
- Retain a selectable reference output and one delayable mirrored output.
- Persist per-member endpoint-volume targets plus mirrored delay and clipping-safe digital gain.
- Allow click-safe live calibration while the group is running.
- Support format conversion/resampling when shared-mode mix formats differ.
- Keep bounded buffering and ongoing clock-drift correction so alignment does not steadily diverge.
- Use short ramps for start, stop, and gain changes.
- Own exactly one runtime instance and clean it up when another target wins, the application exits, or a transition is cancelled.
- Define explicit missing-member behavior and never fall back to an unconfigured endpoint.
- Include endpoint states, formats, delay/gain, runtime state, clock offset, corrections, underruns, and native failures in Health and support bundles.

### Equalizer APO profiles

- Allow an Audio Target, Activity, or Source to select a named profile or an explicit Off/bypass profile.
- Do not require Peace.
- Detect whether Equalizer APO is installed and attached to the selected endpoint.
- Import or reference named Equalizer APO text profiles; do not build a general parametric-EQ editor.
- Manage a small PCMode-owned include file instead of replacing the user's main configuration.
- Make the one-time include insertion user-approved, backed up, atomic, and recoverable.
- Preserve unrelated Equalizer APO configuration.
- Detect missing profiles and likely stacking/conflicts with Peace or another manager.
- Report the selected profile, affected endpoint, provider availability, and configuration problems in Health.
- State the verification boundary honestly: PCMode can verify file selection and attachment state, not prove audible DSP output through a documented control API.
- Complete GPLv2 and redistribution review before bundling or installing any dependency.

### Acceptance criteria

- A user can create multiple named single-output and two-output Audio Targets in the GUI.
- Selecting a normal output stops a group; selecting a group establishes its reference and starts one mirror runtime.
- Repeated activation never creates duplicate capture/render loops.
- Configured default roles, volume, and mute values are read back and verified.
- Missing, disabled, unplugged, and replaced endpoints produce distinct actionable states with no silent rerouting.
- A normal shared-mode source remains perceptually aligned during a one-hour two-device soak without steadily increasing echo, repeated glitches, or obvious pitch changes.
- Leaving a group stops capture/render promptly and leaves no hidden runtime.
- Named Equalizer APO selection and Off/bypass are safe, recoverable, and preserve unrelated configuration.
- Automated tests use fakeable native seams and never play through the test machine's real devices.
- A manual hardware matrix covers endpoint roles, HDMI arrival, format mismatch, disconnect/reconnect, profile configuration, and long-session drift.

### Deferred

- More than two synchronized outputs per group.
- Arbitrary per-member clock topologies, automatic latency measurement, or microphone-assisted calibration.
- Audio-session inspection, per-application volume, and arbitrary per-application output routing.
- Microphone/capture control, network audio, guaranteed Bluetooth synchronization, and exclusive/DRM guarantees.
- A custom virtual audio endpoint or driver.

## Step 8 — Add external Sources and safe handoffs

### Outcome

PCMode can orchestrate activities whose content comes from an HDMI device rather than the Windows PC.

### Required work

- Add a generic External HDMI Source with display name, target TV/provider, input alias, and optional availability, CEC identity, or wake capability.
- Reuse provider capabilities; do not require every TV to support every optional Source operation.
- Express handoff ordering through the existing dependency/planning system.
- When moving from a PC Source to an external Source, prepare shared TV/audio state and switch input last.
- When moving to a PC Source, prepare display and audio state, launch and verify the application, then switch the TV input to the PC last.
- Add Source exit policies: keep running, stop, ask when ambiguity matters, minimize/suspend where supported, or switch away when PCMode cannot stop the device.
- Keep defaults simple and place uncommon exit policies in advanced settings.

### Acceptance criteria

- A user can add and activate a generic console or streaming-device Source without a bespoke device integration.
- TV input never switches to an unready destination when readiness can be verified.
- A failed destination leaves the user with visible state and Retry/Repair options.
- Leaving a Source follows its configured policy and does not destroy user state unexpectedly.
- Planning, cancellation, unavailable-device, and keep-running behavior have automated tests.

### Deferred

- Console-specific APIs and automatic console discovery.
- Universal power/wake promises when no reliable provider capability exists.

## Step 9 — Complete couch and controller UX

### Outcome

A controller user can enter, understand, repair, and leave normal TV activities without reaching for a keyboard or mouse.

### Required work

- Present the same Space → Activity → Source model through a compact keyboard/mouse picker and a controller-oriented 10-foot picker.
- Allow an Activity to define a preferred Source while retaining access to alternate Sources.
- Add a reliable global activation chord with controller selection, reconnect/disconnect handling, debounce, and hold/chord protection against gameplay accidents.
- Provide consistent confirm, back/cancel, selection state, and optional haptic feedback.
- Add direct shortcuts for common targets, including return to Desk, that work when the TV is off.
- Show transition progress, Health, Retry, Repair, and partial failures in a couch-readable overlay.
- Ensure focus and dismissal behavior returns control to the expected application.
- Optionally add Controller Mouse as an explicitly invoked fallback for unavoidable mouse-only Windows UI, with visible state and no interference during normal gameplay.

### Acceptance criteria

- Normal supported couch flows require no keyboard/mouse input.
- Controller disconnect/reconnect does not strand or duplicate the picker.
- Gameplay input does not accidentally trigger the global picker under the configured activation gesture.
- Failed transitions can be retried or repaired from the controller overlay.
- Direct return-to-Desk works without relying on the TV display.
- Controller Mouse, if included, is off during normal gameplay and clearly indicated when active.

### Deferred

- A mobile, TV-native, or remote web frontend.
- General accessibility remapping beyond the supported controller interaction model.

## Step 10 — Add bounded DDC/CI support

### Outcome

Supported computer-monitor controls can participate in desired state without making monitor quirks a critical path for the rest of PCMode.

### Required work

- Discover DDC/CI capabilities per monitor and retain the monitor's stable logical alias.
- Start with controls that can be read, written, and verified reliably, especially brightness and input.
- Add contrast, color temperature, volume, or power only when capability detection and hardware behavior justify them.
- Hide unsupported controls and mark uncertain capabilities honestly.
- Integrate supported properties with validation, planning, readback, Health, and Repair.
- Bound probing and command timeouts so a sleeping, disconnected, or non-compliant monitor cannot stall an entire transition.
- Treat DDC resources as optional unless the user explicitly marks them required.
- Record raw capability/protocol failures in diagnostics without exposing them in normal configuration.

### Acceptance criteria

- Supported brightness and input changes are read back and verified on representative monitors.
- Unsupported controls are not offered as usable settings.
- Failed probing or commands do not destabilize unrelated resources or transitions.
- Missing monitors and unsupported VCP codes produce clear Health states.
- Automated tests cover capability variation, timeouts, readback mismatch, and optional-resource failure.

### Deferred

- Exhaustive support for every VCP command or monitor firmware quirk.
- Broad compatibility work not justified by v1 beta hardware.

## Step 11 — Build guided onboarding

### Outcome

A new user reaches a useful Desk or TV configuration without understanding provider names, hardware IDs, or configuration schema.

### Required work

- Discover displays, audio endpoints in all reported states, supported TVs, DDC capabilities, common applications, controllers, OpenRGB, and Equalizer APO attachment.
- Ask the user to identify devices with friendly questions rather than raw IDs.
- Offer small, editable Desk and TV starter configurations based on discovered hardware.
- Add PC application Sources and allow external Sources to be entered manually.
- Configure normal Audio Targets, named synchronized groups, reference endpoints, and optional Equalizer APO profiles.
- Show unsupported or limited hardware before the user depends on it.
- Preview the complete desired-state change before the first activation.
- Provide a guided test that reports failures and links directly to the relevant configuration or repair action.
- Allow users to skip optional integrations and return to onboarding later.

### Acceptance criteria

- A clean installation can create and test a Desk-only configuration without JSON.
- A supported TV user can create and test a TV activity and Source.
- Audio, DDC, and optional integrations are only proposed when discovery indicates availability.
- Preview accurately describes the important state changes before applying them.
- Failed tests preserve the configuration draft and provide an actionable correction path.

### Deferred

- Cloud-backed setup or account creation.
- Automatic assumptions that silently bind uncertain hardware matches.

## Step 12 — Add activation and configuration lifecycle

### Outcome

Every activation surface uses one contract, and configurations remain safe across releases and hardware changes.

### Required work

- Define one canonical operation: `Activate(Space, Activity, Source)`.
- Route GUI and controller activation through that operation.
- Add a basic CLI for activate, status, target listing, and Health.
- Ship the initial Space → Activity → Source configuration as schema v1 and define forward migration chains for later releases.
- For future schema changes, back up before migration, validate the result, roll back on failure, and reject configurations newer than the running application.
- Add restore of the previous valid configuration and reset while preserving a recovery copy.
- Add import/export of portable logical configuration without assuming hardware IDs are portable.
- Prompt the user to map logical aliases to local hardware when importing on another machine.
- Offer conservative, user-confirmed remapping when a likely replacement device appears.

### Acceptance criteria

- GUI, controller, and CLI produce the same activation semantics and transition state.
- The shipping configuration is identified as schema v1.
- Every later released schema-to-schema path must have migration tests.
- A failed future migration leaves the last valid configuration recoverable and active.
- Import on different hardware requests explicit mappings instead of guessing silently.
- Export excludes credentials and machine-specific data that should not travel.

### Deferred

- URI activation, richer local IPC, and Stream Deck integration.
- Cloud synchronization or database-backed configuration.

## Step 13 — Finish diagnostics and recovery

### Outcome

PCMode remains operable when configuration or hardware is broken, and users can provide enough sanitized evidence to diagnose a problem.

### Required work

- Open the application even when the active configuration cannot be loaded.
- Provide recovery actions for viewing the problem, restoring a backup, importing, resetting with a recovery copy, and opening diagnostics.
- Generate a sanitized support bundle containing PCMode/Windows versions, discovery summary, provider status, current target, last transition, recent logs, validation results, and redacted configuration.
- Automatically remove credentials, tokens, secrets, and private paths where appropriate.
- Add health checks for missing or changed displays/audio devices, stale mappings, unexpected audio roles, stopped or duplicated audio-group runtimes, drift/underrun problems, Equalizer APO configuration, missing applications, unreachable TVs, DDC failures, optional provider availability, backups, and startup registration.
- Ensure every health finding has a stable identifier, severity, plain-language summary, technical detail, and suggested action where one exists.

### Acceptance criteria

- A malformed configuration never reduces recovery to manually locating an AppData file.
- Restore and reset paths preserve a recoverable copy of prior user data.
- Support bundles contain the necessary diagnostic categories and pass redaction tests.
- Health checks distinguish configuration, availability, capability, and runtime failures.
- Recovery behavior is tested for invalid syntax, invalid schema, migration failure, missing backups, and startup-provider failure.

## Step 14 — Build commercial release infrastructure

### Outcome

PCMode installs, upgrades, updates, recovers, and uninstalls like a trustworthy paid Windows application.

### Required work

- Produce one canonical self-contained Windows release artifact that feeds portable and installed distributions.
- Add a clean per-user installer where practical, Start menu integration, and configurable startup registration.
- Preserve user configuration during normal upgrades.
- Provide an explicit user-data removal choice during uninstall.
- Sign executables, installer, and update packages.
- Add update discovery, release notes, safe download/install, restart, migration, and failure recovery.
- Use explicit versioned releases and automate build, tests, packaging, checksums, and release artifacts.
- Review licenses and redistribution obligations for all bundled or optionally installed integrations, including Equalizer APO handling.
- Do not require an account merely to install or receive updates.

### Acceptance criteria

- Clean install, in-place upgrade, failed upgrade recovery, and uninstall pass on supported Windows versions.
- A normal upgrade preserves configuration and migrates it exactly once.
- Release artifacts and update payloads are signed and checksummed.
- The updater cannot leave the application without either the new working version or a documented recovery route.
- The canonical artifact is reproducible through the release pipeline.

## Step 15 — Run a broad hardware beta

### Outcome

Paid v1 is proven outside the developer's own hardware and configuration.

### Required coverage

- Supported Windows 10 and 11 versions.
- AMD, NVIDIA, and Intel graphics.
- Single, dual, triple, ultrawide, laptop/dock, TV, and disconnected-display arrangements.
- Deep LG TV coverage plus clean behavior when no supported TV exists.
- DDC-capable, partially capable, unsupported, sleeping, and disconnected monitors.
- Multiple onboard, USB, HDMI, eARC, and changed-identity audio devices.
- Multiple named Audio Targets and Party-style groups with different shared-mode formats.
- Equalizer APO absent, attached, unattached, missing-profile, conflicting-manager, and working-profile states.
- Different controllers and controller disconnect/reconnect behavior.
- OpenRGB installed, unavailable, and absent.

Test the complete lifecycle: installation, first run, configuration, migration, activation, superseding transitions, partial failure, Repair, backup/restore, import/export, update, uninstall/reinstall, diagnostics, and support-bundle generation.

For synchronized audio, include endpoint disconnect/reconnect, default-role changes, format mismatches, repeated activation, runtime cleanup, and at least one-hour drift/underrun soak tests across multiple real speaker/soundbar combinations.

### Exit criteria

- All release-blocking crashes, data-loss risks, unsafe recovery paths, and repeatable core-transition failures are fixed.
- Known hardware limitations are documented and shown before activation where relevant.
- Installation, migration, update, recovery, and uninstall are reliable on the supported matrix.
- Party/generalized audio and DDC behavior meet their stated supported boundaries.
- Remaining non-blocking issues are triaged with an explicit post-v1 disposition.

## Step 16 — **SHIP THE FIRST PAID RELEASE**

This is the exact paid-v1 release point. Do not charge before Steps 1–15 are complete.

### Paid-v1 release gate

- Normal setup and use require no JSON editing.
- Space → Activity → Source is stable, understandable, and stored in the initial schema v1 format.
- Desk and TV workflows are useful, reliable, and recoverable.
- Existing LG support remains strong.
- External HDMI Sources and Source handoffs behave predictably.
- Controller users can activate, monitor, retry, repair, and leave normal couch flows without keyboard/mouse input.
- Single-output and named two-output Audio Targets are reliable within their disclosed scope.
- Equalizer APO profile selection is safe and recoverable when the optional dependency is present.
- DDC/CI works for the advertised controls without destabilizing unsupported monitors.
- Health, Repair, transition progress, diagnostics, configuration recovery, and support bundles work.
- Installer, signing, updates, migrations, and rollback/recovery have passed beta testing.
- Major hardware-beta blockers are resolved and third-party licensing is cleared.

Position the product around the outcome:

> **PCMode configures your entire setup around what you want to do.**

Pricing can follow the existing one-time-purchase direction. It does not change the engineering release gate.

---

# Optional and post-v1 expansion

Post-v1 steps are ordered recommendations, not promises. Reprioritize them using support requests, beta/paid usage, hardware prevalence, API reliability, maintenance cost, and commercial impact.

## Step 17 — Broaden display and device support

### Outcome

PCMode supports more display control and AV hardware without weakening capability-driven behavior.

### Candidate work

- Add HDR as a first-class desired-state resource with capability detection, readback, validation, verification, Health, Repair, and clear unsupported-display handling.
- Add explicit display-resolution selection, readback, validation, and verification.
- Broaden DDC/CI compatibility using real hardware reports while retaining strict timeouts and capability checks.
- Generalize TV capability contracts without forcing LG-specific features onto other providers.
- Add Samsung, Sony, or generic CEC support according to demand and API reliability.
- Consider AV receivers, soundbars, projectors, HDMI matrices/switches, KVMs, and streaming devices when they directly participate in a Space or Activity.

### Done when

- Each provider advertises only capabilities it can support and verify.
- Unsupported capabilities never appear as normal usable controls.
- New resources participate in validation, planning, Health, Repair, and diagnostics.
- Provider additions include hardware or deterministic protocol tests and documented limitations.

## Step 18 — Expand Windows environment resources

### Outcome

PCMode covers additional high-value state for work, gaming, dock, simulation, and creator workflows.

### Candidate work

- Add a Windows Performance resource for power plans/power mode.
- Add carefully scoped process-priority policy for selected applications only when it provides a proven benefit.
- Read current state and converge through the normal desired-state engine.
- Add a Window Layout resource that captures and restores monitor, position, size, and maximized state for matched applications.
- Tolerate missing applications and report layout failures without failing unrelated transition work.
- Expand Desk, laptop/dock, Sim Rig, Studio, and creator templates using validated demand.

### Boundaries

- Do not become a generic Windows optimizer or apply unsafe/undocumented tweaks.
- Do not try to replace dedicated general-purpose window managers.
- Keep both features as verified desired-state resources, not session scripts.

## Step 19 — Add advanced audio capabilities

### Outcome

Expand the paid-v1 Audio Target model only where real usage justifies the additional synchronization and support burden.

### Candidate work

- Support more than two synchronized outputs and arbitrary per-member buffering/clock correction.
- Add group master level and richer missing-member policies.
- Consider automatic latency measurement or microphone-assisted calibration.
- Add read-only session inspection before opt-in per-application volume/mute controls.
- Restore temporary session overrides only when PCMode still owns the value and the user has not changed it manually.
- Add microphone/capture endpoint discovery, defaults, gain, and mute as a separate privacy-explicit resource.
- Add other DSP integrations or richer Equalizer APO tooling when profile selection proves insufficient.

### Boundaries

- Do not promise arbitrary per-application output routing unless it proves supportable across released Windows versions.
- Do not guarantee Bluetooth, exclusive-mode, or DRM-protected synchronization.
- Do not inspect, record, or transmit microphone content.
- Build a custom virtual endpoint/driver only when proven requirements cannot be satisfied more simply and the signing/update/support burden is justified.

## Step 20 — Open integration surfaces

### Outcome

Users can activate and inspect PCMode from other local workflows without creating an insecure remote-control surface.

### Candidate work

- Add URI activation for shortcuts and launchers.
- Add a local-only IPC/API for activation, target listing, state, and Health.
- Add richer Stream Deck integration through the canonical activation operation.
- Consider transition-state subscriptions for local integrations.

### Done when

- Every surface calls the same canonical activation contract.
- Authentication and transport boundaries are explicit.
- Remote network access remains disabled by default.
- PCMode never exposes an unauthenticated LAN API.

## Step 21 — Publish provider-development support

### Outcome

Contributors can add hardware support without reverse-engineering the orchestration engine.

### Candidate work

- Stabilize contracts for discovery, validation, readback, apply, verification, availability, capabilities, dependencies, convergence, cancellation, and diagnostics.
- Document the provider lifecycle, desired-state rules, optional/required behavior, error handling, timeouts, and logging.
- Publish a minimal sample provider with deterministic tests.
- Provide a harness for success, drift, failure, timeout, dependency, and cancellation behavior.
- Initially accept providers in the main source tree so compatibility ships with PCMode releases.

### Deferred

- Arbitrary DLL loading, plugin sandboxing/signing, compatibility negotiation, and a plugin marketplace until real contribution volume justifies them.

## Step 22 — Expand providers based on real demand

### Outcome

Hardware and integration breadth grows according to user value rather than technical novelty.

### Prioritization criteria

- volume and severity of support requests;
- hardware prevalence among actual users;
- reliability and legality of the available API/protocol;
- implementation and long-term support cost;
- impact on purchase, retention, or a major supported workflow.

Potential work already within the product direction includes deeper TV support, AV receivers, soundbars, projectors, CEC, streaming devices, HDMI matrices, KVMs, audio interfaces/DSPs, richer Stream Deck support, and a limited Home Assistant scene bridge.

Do not turn PCMode into a Home Assistant replacement or general IoT automation platform.

## Step 23 — Revisit coordinator/agent architecture

### Outcome

Only if user demand proves it, PCMode can coordinate resources beyond the local Windows process without changing the intent model.

### Possible evolution

```text
Coordinator
├── Space / Activity / Source
├── desired state and planning
└── Health and orchestration

Windows Agent
├── displays, audio, and applications
└── theme, DND, cursor, and RGB

Device Providers
└── TVs, monitors, AV equipment, CEC, and other devices
```

Initially, the Coordinator and Windows Agent remain in the same process. Remote Windows agents, multiple PCs, a home-server coordinator, or other frontends are considered only when they materially improve a demonstrated workflow.

### Boundaries

- Do not add remote networking merely for architectural purity.
- Do not make cloud accounts a prerequisite.
- Preserve the same canonical activation, desired-state, capability, verification, and recovery semantics.

---

# Architecture at a glance

```text
GUI / controller / CLI / future integrations
                     ↓
       Activate(Space, Activity, Source)
                     ↓
       Compose desired resource state
                     ↓
       Plan dependencies and transitions
                     ↓
          Capability-driven providers
                     ↓
       Read → Apply → Verify → Health / Repair
```

## Rules to preserve

- Model desired state, not ordered macros or a generic scripting language.
- Use one canonical activation path for every UI and integration.
- Use stable logical IDs; keep raw hardware identity inside configuration/provider boundaries.
- Let providers own platform-specific behavior and expose only supported capabilities.
- Treat resources as required or optional explicitly.
- Preserve correct current state and apply only meaningful differences.
- Verify changes whenever the platform provides reliable readback.
- Supersede old requests safely at resource boundaries.
- Keep UI, intent composition, planning, providers, and native compatibility seams separable.
- Keep local and possible future remote execution conceptually separable without adding a network layer now.
- Make native seams fakeable so automated tests do not manipulate the development machine's real hardware.

# Product boundary

PCMode orchestrates devices that directly participate in computing, entertainment, productivity, simulation, creator, or AV activities.

Appropriate resources include PCs, displays, TVs, projectors, consoles, streaming devices, AV receivers, soundbars, audio interfaces, DSPs, HDMI switches, KVMs, controllers, RGB, and directly relevant room lighting.

Unrelated household automation—such as thermostats, garage doors, or sprinklers—belongs in systems such as Home Assistant. PCMode may invoke a relevant scene later rather than becoming a general home-automation system.

Do not let cloud accounts, cloud sync, mobile or TV-native apps, dedicated hardware, a runtime plugin marketplace, a generic scripting language, DRM-heavy licensing, enterprise fleet management, or unrelated IoT work delay the core product.
