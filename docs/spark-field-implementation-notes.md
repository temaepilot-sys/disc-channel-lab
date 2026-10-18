# Spark and spectral-veil implementation notes

The current modes are Particles, Spark Field, Hybrid · Spectral Veil, and Aurora + Spark Field. Public and personal builds use the same viewer sources. Earlier Electric Flow and standalone Aurora rendering code is retired; only compatible preference records remain so older settings can be read.

## Data and rendering

- The existing player PCM tap and named pipe supply channel layouts, speaker enable states, current spectra and fast light levels. The viewer never decodes or plays linked audio.
- Spark Field extracts per-band energy and positive changes. A bounded float-array pool holds micro sparks, short streaks and long-lived soft motes. A fixed streaming buffer draws them in one call; no particle GameObjects, textures, framebuffers or feedback passes are added.
- Spark capacity is 3,000 / 8,000 / 16,000 for Low / Medium / High. Slots are reserved per source with a shared allowance, preventing one loud source from occupying the whole pool. True silence creates no background particles.
- The veil passes each channel's individual band energies and displacement to one indexed mesh. Higher bands sit higher; displacement and attacks are band-specific. Optional vertical flow moves light accents through the active bands without moving the frequency-color map.
- Matching colors call the same Spark frequency-color function for every band. The veil keeps its own opacity; Spark retains its white-hot cores and small color variations.
- Aurora + Spark draws the veil first, then the sparks, and leaves the old particle pool and speaker beams idle. It defaults to 30 fps, or 60 fps when both quality controls are High. Inactive renderers retain reusable GPU resources but do no update or drawing work.
- Pause freezes visual time. Source/signal changes, seek and disconnect discard stale light. Player Mute/Solo excludes both layers immediately. Final output uses the two actual output channels.

## Camera, settings and reopening

Camera navigation shares the existing view matrix and headphone scene transform. Home resets the selected camera mode without changing its selection. Free movement remembers the preset it departed from in `camera.homeView`, including in saved JSON. Earlier Free settings without this field fall back to the Overview home position while remaining in Free movement.

The player sends a small control packet to reopen or activate an existing viewer host. Browser launch happens on a separate worker, never on the PCM processing path. Bootstrap returns analysis settings, the current session view and an audio snapshot in one request. Explicit Save/Load settings remains independent of the in-memory reopen state.

Listener-synchronized Particles need real future PCM. Emission modes skip future FFTs but retain a bounded raw-packet window without copying payloads: at most 384 packets, 24 MiB and an 8.3-second timestamp span. A switch back to Listener sync analyzes small asynchronous batches, rather than waiting for the entire read-ahead period. Epoch and mixer-generation changes invalidate old packets. No synthetic future spectrum is invented.

## Verification

From the repository root, after `./build.ps1`:

```powershell
./tests/verify-space-sketch.ps1
./tests/verify-space-sketch.ps1 -Browser
```

The first command builds the verification project, generates its own demo, runs viewer self-tests, actual named-pipe/host tests, native window reopen checks, renderer controls, camera navigation, band-motion checks, and shader failure recovery. The second also launches an isolated installed Chrome profile for actual Home/WebGL and combined-renderer/PCM checks. It does not use the person's ordinary browser profile. Node.js is a development-test dependency only.

To test another build, use the runner's `-ViewerDirectory` parameter. Individual browser scripts accept `LAB_VIEWER_EXE`; the dotnet helper accepts `LAB_DOTNET` and otherwise locates a local `.dotnet10/sdk` or uses `dotnet` on PATH. No developer-specific home directory is required.

The Spark browser/stress scripts additionally support `SPARK_CDP_PORT` and an already running isolated Chromium debugging profile. For the optional native audio queue check, build `DiscChannelLab.Verify` and set `AURORA_SPARK_AUDIO_VERIFY` to its DLL before running `tests/aurora-spark-browser.cjs`. Supply the user's FFmpeg tools on PATH.

## What the measurements establish

During development, real 1080p WebGL on an RTX 3070 Ti sustained the selected 30/60 fps caps for the sampled scenes. Combined Medium measured roughly 0.2 ms veil update and 0.7 ms Spark update CPU time at p95; draw-submission CPU time was roughly 2.3 / 1.8 ms. These are CPU measurements and scene-specific results, not GPU execution times or universal performance guarantees.

Native Stereo/HRTF queue tests ran during High combined rendering with zero empty buffers. Those checks used a test output sink and establish queue behavior rather than physical-device acoustic latency. A retained-PCM startup scenario reduced first visible non-LFE particles from about 7.3 seconds to about 0.34 seconds in a dedicated existing Chrome process. Browser process launch was outside that timing.

All particles, veil motion, frequency heights, colors, apparent lifetime and waterfall flow are artistic representations of spectral activity. They do not reconstruct acoustic phase, measured room pressure, reflections or a physical sound field. Other GPUs, browsers, audio devices and long sessions still need their own checks. [User controls](visualizer.md) · [Update summary](changes-2026-10-18.md).
