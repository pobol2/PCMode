# PCMode audio architecture

PCMode audio has a desired-state control plane and a separate real-time data plane. `PCMode.Engine` decides which audio value a target wants. The native driver, `PCMode.Audio.Host`, and the projects below Host move and process PCM. This separation keeps real-time buffers, device clocks, WASAPI streams, and driver handles out of the Tray and normal Engine transition loop.

## Two audio behaviors

The Engine's `audio` resource accepts two kinds of value:

- A **device alias** resolves a configured Windows playback-device name to its current endpoint ID, stops any active Host graph, and makes that physical endpoint the Windows default. It requires neither Host nor the PCMode driver.
- An **audio target** resolves one or more route aliases, applies the route graph to Host, and makes the PCMode virtual render endpoint the Windows default. Applications then render into PCMode and Host fans the PCM out to the target's physical routes.

They are values of one mutually exclusive resource. There is no separate “multi-output mode” transition path.

## End-to-end flow

```text
CONTROL PLANE

Space / Activity / Source
          |
          v
    ModeEngine -> AudioProvider -> IAudioRuntime / AudioHostLink
                                      |
                                      v  named pipe, protocol v1
                                PCMode.Audio.Host

DATA PLANE FOR AN AUDIO TARGET

Windows applications
          |
          v
PCMode virtual render endpoint
  ACX/KMDF kernel driver
          |
          v  protocol 1.1 buffered IOCTL packets
PCMode.Audio.DriverClient
          |
          v
150 ms input jitter buffer
          |
          v
PCMode.Audio.Engine, 480-frame blocks
          |
          +--> route A: EQ -> gain/mute -> adaptive resample -> delay buffer -> WASAPI A
          +--> route B: EQ -> gain/mute -> adaptive resample -> delay buffer -> WASAPI B
          `--> additional routes use the same independent route pipeline
```

The production input is the driver. Host may instead use WASAPI loopback only when explicitly launched with the development source option.

## Project ownership

| Project | Responsibility |
|---|---|
| `PCMode.Audio.Core` | Audio identities and formats, target/route/health models, audio blocks, gain, biquads, parametric EQ, and processing chains. |
| `PCMode.Audio.Engine` | Live input-to-routes graph, per-route processing and delay, buffering, resampling, clock estimation, drift correction, synchronization, health, and route recovery primitives. |
| `PCMode.Audio.Windows` | Core Audio endpoint catalog, WASAPI loopback/render streams, shared-mode format conversion, audio-clock snapshots, and MMCSS registration. |
| `PCMode.Audio.DriverClient` | Device-interface discovery, driver protocol validation, IOCTL connection/status/read operations, PCM conversion, staging, and reconnect. |
| `PCMode.Audio.Protocol` | Versioned command/response DTOs and bounded named-pipe framing shared across the process boundary. |
| `PCMode.Audio.Client` | Short-lived Host connections, request/response handling, compatibility checks, and Host launch/readiness logic. |
| `PCMode.Audio.Host` | Separate process that owns the live Engine, driver input, WASAPI outputs, command queue, named-pipe server, health, and recovery loop. |
| `PCMode.Engine` audio provider | Configuration parsing, alias resolution, dependency declarations, endpoint volume/default changes, Source-resource integration, and the Host control adapter. |
| `driver/PCMode.Audio.VirtualEndpoint` | Native x64 ACX/KMDF virtual render endpoint and single-consumer PCM transport. |

Dependency direction is deliberate. `PCMode.Engine` references Audio Core, Protocol, and Client, but not Audio Engine, Windows, DriverClient, or Host. Tray's Host project reference orders packaging only (`ReferenceOutputAssembly="false"`).

## Virtual endpoint and driver

The driver package creates the root-enumerated `ROOT\PCMODEAUDIO` device and the render endpoint whose Windows name contains `PCMode Virtual Audio Endpoint`. The current INF targets Windows 11 x64 build 22000 or newer. The project uses Windows SDK/WDK `10.0.28000.0`, ACX 1.1, and KMDF 1.33.

The ACX circuit advertises fixed 48 kHz, two-channel PCM16 and float32 render formats. Windows applications see a normal render endpoint. ACX supplies render packets; the driver's render stream advances them on the ACX event/timer schedule and forwards complete rendered regions to the transport. Oversized packets are dropped rather than truncated.

This is a kernel driver and is built outside `PCMode.slnx`. Installing or updating it requires elevation, x64 DevCon, and a catalog/signature Windows accepts. The development rebuild script does not configure trust, test-signing, Secure Boot, or production signing. See [SECURITY.md](../SECURITY.md) and [BUILD_AND_TEST.md](BUILD_AND_TEST.md).

## Driver-to-user-mode PCM transport

The transport is a versioned device-interface/IOCTL contract; it is not a named pipe and does not expose the ACX ring directly to user mode.

Protocol 1.1 currently provides:

- `GET_PROTOCOL`, `GET_STATUS`, `READ_PCM`, `CONNECT`, and `DISCONNECT` buffered IOCTLs;
- protocol version, capability bits, preferred format, packet-size limit, and slot count;
- device/stream/consumer state plus rendered byte/packet and dropped-packet counters;
- packet sequence, QPC timestamp, format, and PCM payload;
- exactly one claimed PCM consumer;
- 32 bounded packet slots and a 32,768-byte maximum packet;
- drop-oldest behavior when the producer overtakes the consumer.

`PCMode.Audio.DriverClient` locates the device through its interface GUID, opens a read-capable handle, validates major/minor compatibility and required capabilities, and explicitly claims the single-consumer transport. It converts PCM16 or float32 payloads to the Engine's interleaved float representation. Packet headers, sizes, alignment, and nominal format are checked before use.

`DriverAudioInput` retains a bounded staging buffer when the Engine requests fewer frames than one driver packet contains. On I/O failure it disconnects and attempts reconnection no more frequently than every 100 ms. A reconnect using a different nominal format is rejected; changing the graph format requires a new graph.

## Audio Host and IPC

Host is a separate, ordinary user-mode process. It is built beside the Tray under `AudioHost/`, but is neither loaded nor controlled through in-process types.

`AudioHostLink` first contacts the default `PCMode.Audio.Host` pipe. It adopts a compatible surviving Host, launches one only when needed, and waits up to the startup budget for readiness. Host deliberately survives Tray exit: if Windows is still rendering into the virtual endpoint, killing Host would turn a harmless shell exit into silence. Switching the `audio` resource to a direct device calls Host `Stop` before changing the Windows default.

Before opening the driver or pipe, Host claims a per-session semaphore derived from the pipe name. A second Host for that pipe exits without touching the driver. The Host process uses sustained-low-latency GC. Its dedicated non-background audio thread runs in the multithreaded apartment and registers with MMCSS `Pro Audio`; control requests are queued to that thread so graph mutation and block processing have one owner. MTA is required, not incidental: an STA thread that touches COM gets a hidden top-level window, and every blocking wait it makes — including the WASAPI render wait for buffer space — becomes a message-pumping wait that dispatches whatever the OS delivers to that window first. A display topology change broadcasts window messages synchronously to every top-level window in the session, so an STA render thread would stall inside its own buffer wait for as long as the slowest recipient of that broadcast took.

The pipe protocol is version 1. Each connection starts with compatibility negotiation and carries length-prefixed camel-case JSON commands and responses, capped at 1 MiB. Commands are:

- apply target;
- recalibrate one route;
- stop the graph;
- get status;
- get health;
- shut down Host.

The runtime queue is bounded at 128 pending commands. Client calls normally open a connection for one request/response exchange. The pipe is local, but the current server does not configure an explicit ACL or application-level client authentication; that limitation is part of the security model rather than an implied trust guarantee.

## Engine graph and physical outputs

An `AudioTarget` contains at least one `AudioRoute`. Each route names a resolved endpoint ID and carries required/optional state, delay, gain/mute, and an optional processing profile. Configuration may also request a Windows endpoint-volume setting before activation.

Host builds one shared input and one independent `AudioRouteRuntime` per route. The default block capacity is 480 frames, which is 10 ms at the current 48 kHz nominal format. `AudioGraph` reads a block once and gives the same source timeline to every operational route.

Each physical route opens a shared-mode, event-driven WASAPI render client. PCMode requests float32 at the Engine's nominal format and enables Windows shared-mode PCM conversion and the default-quality system sample-rate converter for the endpoint boundary. The route also obtains `IAudioClock`; played position and submitted position form the timing snapshot used by synchronization.

Route startup rules are:

- failure of a required route fails target startup;
- an optional route may remain unavailable while healthy routes play;
- at least one route must start;
- failure of the shared input fails the graph.

Endpoint aliases are resolved immediately before activation because HDMI and other endpoints can appear or disappear with display state. Duplicate aliases that resolve to the same live endpoint are refused for one target.

## Buffering, delay, resampling, and synchronization

The driver and WASAPI callbacks do not share a cadence, and physical endpoints have independent clocks. PCMode handles those differences at two levels.

First, `JitterBufferedAudioInput` pumps the bursty driver input on its own background thread into an `AudioRingBuffer`. It primes 150 ms of silence and allocates four times that cushion. The Engine-facing read always advances a complete requested block, zero-filling and counting an underrun only if the cushion is exhausted. If the pump itself fails, the failure is surfaced rather than hidden as silence.

Second, every output route owns its own:

- configured-delay ring buffer, primed with the requested silence;
- output-clock estimator;
- drift controller;
- adaptive resampler;
- underrun and resynchronization counters.

The clock estimator compares the shared source frame position with the physical output's played/submitted clock and slowly learns the endpoint's normal queue depth. Normal correction is slew-limited and bounded to ±500 ppm. If timing error reaches 20 ms, the route enters bounded resynchronization, up to ±20,000 ppm, and leaves that state once error falls to 5 ms. The adaptive ratio changes that route's sample count; it does not alter the shared source timeline or another route's timing.

The configured route delay and adaptive clock correction therefore solve different problems: delay aligns a known fixed offset, while resampling tracks ongoing clock-rate and queue-depth differences.

### Stall detection and recovery

A route-level stall and an output-level stall are detected independently and answer different questions. `AudioRouteRuntime` times the wall-clock gap between successive `Process` calls; a gap well past what the block size should ever take resets that route's resampler, synchronizer, EQ, and gain smoothing state rather than letting the synchronizer fight a fabricated drift it never actually saw happen. This does not fade the route's own output, because the block computed right after the gap is normally an ordinary continuation of the source waveform.

Independently, `WasapiRenderOutput` tracks its own contact with the physical device. `IAudioClient.GetCurrentPadding` returning zero means every frame ever handed to the endpoint has already been consumed — the device was fed silence in place of real audio, with no threshold needed because WASAPI is reporting the discontinuity directly rather than this code inferring one from elapsed time. A wall-clock backstop, scaled to a multiple of the endpoint's own buffer duration, catches the case where a gap opens and closes between two padding reads. Either trigger arms a short (~5 ms) linear fade-in on the next audio actually submitted, so resumed playback does not jump to full amplitude at an arbitrary phase; the fade skips silent frames rather than spending its ramp on them, and re-arming an in-progress fade is a no-op so one interruption cannot restart the ramp partway through.

Both layers report their counters through health: route-level `StallRecoveryCount`/`LongestProcessGapMilliseconds`, and output-level `OutputRecoveryFadeCount`/`OutputEndpointDryCount`/`LongestOutputSubmitGapMilliseconds`/`LongestOutputWriteMilliseconds`/`LongestOutputBufferWaitMilliseconds`. The two accounts do not have to agree, and which one moves for a given interruption says where it happened: a route-level gap with no endpoint dry-out means the device kept playing throughout; an endpoint dry-out with no route-level gap means the interruption never grew large enough to show up between `Process` calls. Splitting the output-level write time from the buffer-wait time inside it further separates a device that stopped consuming and later resumed (wait ≈ write) from an interop call blocking outright (write ≫ wait).

## DSP and live calibration

Every route has the same processing order:

```text
parametric EQ -> gain / mute / route-volume multiplier
```

A named processing profile contains a preamp from -24 dB to +24 dB and zero or more biquad EQ bands. Routes may reuse a profile or remain flat. EQ state is independent per channel. Live EQ replacement crossfades over 128 frames, and live gain/mute changes ramp over 256 frames to avoid clicks.

Recalibrating gain, mute, volume multiplier, or EQ updates an operational route in place. Delay determines ring-buffer sizing, so changing delay rebuilds only that route and leaves the shared input and other routes running. The desired target is updated even if that replacement fails, allowing recovery to retry the requested calibration.

## Health and recovery

Health is owned by the real-time subsystem and returned through the Host protocol. It includes Host/process state, active target, input state, every configured route, endpoint and format state, configured delay/processing, timing error, drift/correction ratio, underrun/resync counts, the route- and output-level stall counters described above, and the last concrete failure.

Recovery is layered:

1. `DriverAudioInput` reconnects a lost driver handle with a short bounded cadence.
2. `AudioEngine` can replace a failed output route for an otherwise equivalent target without rebuilding healthy routes.
3. Input failure or a materially different target rebuilds the graph.
4. `AudioHostRuntime` retains `_desiredTarget` and retries recovery with exponential delay from 250 ms to 5 seconds.
5. `AudioProvider` participates in normal ModeEngine convergence: up to six attempts two seconds apart, with polling and verification, to tolerate delayed HDMI endpoints and contested Windows defaults.

Optional route failure is degraded health and remains eligible for recovery. Required route or input failure prevents the audio target from verifying. Stopping clears Host's desired target and recovery schedule.

## Integration with Source activation

Audio is an ordinary resource in Space -> Activity -> Source composition. A Source may inherit its Activity's audio value, override it with a direct alias or audio target, or explicitly clear it. `SourceKind` does not branch the audio implementation.

For an audio target, `AudioProvider` performs these control-plane steps:

1. Resolve all configured output aliases and the virtual endpoint from one live endpoint snapshot.
2. Apply requested physical endpoint-volume settings, retaining readable prior values for best-effort rollback.
3. Compose the engine target with concrete endpoint IDs and named processing profiles.
4. Ask `AudioHostLink` to apply the graph.
5. Make the virtual endpoint the Windows default and read it back.

Routes are started before Windows is pointed at the virtual endpoint. If Host application fails, touched endpoint volumes are restored. If the default-endpoint step fails, the previous default and readable volumes are restored, while Host retains the desired graph so the provider's next convergence attempt can finish idempotently.

Verification requires both facts: the virtual endpoint is the Windows default and Host reports the requested target running. A reachable Host holding a failed target is not reported as success.

## Tests

Audio tests follow project ownership:

- Core: models, buffers, processing, EQ response, and live reconfiguration;
- Engine: graph, route buffering, clocks, drift, resampling, synchronization, health, and recovery;
- Windows: format and WASAPI boundary contracts;
- DriverClient: native structure/protocol validation, connection, packet conversion, and reconnect behavior;
- Protocol: framing and version compatibility;
- Integration: Host runtime, server/client, command, ownership, and recovery behavior with fakes;
- Hardware: opt-in driver, Host, endpoint, multi-output, reconnect, calibration, and soak checks.

Commands and hardware-test safety requirements are in [BUILD_AND_TEST.md](BUILD_AND_TEST.md).
