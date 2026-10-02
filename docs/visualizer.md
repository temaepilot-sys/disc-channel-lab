# Space sketch — 3D audio visualizer

[日本語](visualizer.ja.md) · [Back to DiscChannelLab](../README.md)

Space sketch is an experimental companion viewer for DiscChannelLab. It turns each channel’s frequency-band energy into colored particles, helping you explore how multichannel recordings fill a listening space. It also works as a place to simply watch music unfold.

## See the light where the channels meet

Multichannel recordings can bring sound from the front, wrap it around you from behind, and fill the space at your feet with bass. Space sketch lets you trace the channel layout and the movement of sound, then watch their frequency-band energy overlap at the listener. Explore the recording, or simply enjoy light gathering with the music and drifting like fireflies.

![Frequency-colored particles glowing together at the listener](screenshots/listener-sound-focus.png)

This screenshot uses **Camera: Overview / Sync at: Listener (sound) / Signal: Input channels**. Listener emphasis works with every camera preset. Raising **Listener brightness** brightens the center while dimming distant particles, bringing the overlapping energy into focus. **Listener size** adjusts the central particle size as well.

Listener sync brings each particle's closest horizontal approach to the listener into approximate alignment with playback of its corresponding audio. The data being visualized is channel energy across frequency bands; color and particle spread make its changes tangible.

## Overview

![Space sketch: overview with multichannel particles](screenshots/visualizer.png)

Low frequencies appear red/orange and low in the scene; high frequencies appear blue/violet and higher up. Each non-LFE speaker emits toward the listener’s horizontal position. **Spread** changes the maximum angle on either side (0–90°, initially ±20°); high bands can be narrower. Height is fixed by logarithmic frequency. Particles continue beyond the listener and fade with age.

LFE emits horizontally through **360° from the center at the listener’s feet**, independently of Spread. It is a symbolic bass source, not a claim about where a physical subwoofer should be placed. No LFE is invented for sources without that channel.

## Listener view

![Space sketch: listener viewpoint](screenshots/listener.png)

Choose **Camera → Listener** to look toward the central particle region from a fixed position about 3 m behind the listener and slightly above it. This makes the gradual enlargement, brightness and vibration visible in front of the camera. Selecting the preset aims slightly downward at the center; drag or use arrow keys to look around. Previously saved viewing angles are preserved; select Listener again to use the new default direction. **Bird’s-eye** provides an elevated view; **Top** looks straight down. Overview, Front and Back are also available.

| Control | Action |
| --- | --- |
| Click the scene, then hold ↑ / ↓ | Tilt through horizontal to straight up/down; in outside views, orbit above/below the scene |
| Hold ← / → | Rotate continuously, beyond a full turn |
| Left drag | Orbit, or look around in Listener view |
| Right drag / wheel | Pan / zoom in outside views; Listener stays at its fixed position |
| Reset camera | Return to Overview |
| Hide UI / Show UI, or H | Hide/restore panels, playback controls, legend and channel labels |
| Save settings, or Ctrl+S | Save the current view and display settings |
| Load saved | Restore the saved settings |

Arrow keys retain their normal behavior while a slider or select box has focus. Click the scene to use camera keys. Camera controls work while playback is paused.

## Connect to the player

1. Build with `./build.ps1` from the repository root. Keep the complete `artifacts/public-win-x64` folder, including its `SpaceSketch` subfolder.
2. Start `DiscChannelLab.exe`, choose a supported audio source, then click **3D Viewer** next to **Open Mixer**.
3. Play audio in DiscChannelLab. The viewer opens separately and follows playback and seeking. Transport and listening volume remain in DiscChannelLab.

The viewer receives audio already decoded by the player. It does not read discs, open WAV/FLAC files, export audio, or play live PCM a second time. **5.1 demo** provides a 40-second synthetic example without a disc; **Link to DiscChannelLab** returns to the player when launched with a link. The demo’s Volume affects only the demo.

For a separately built viewer, select its `DiscChannelLab.Visualizer.exe` when prompted. Old player builds without the **3D Viewer** button do not provide the link.

### Requirements

- Windows x64, Microsoft Edge with WebGL support.
- .NET 10 SDK to build. The public output is framework-dependent: install both the **.NET 10 Desktop Runtime** and **ASP.NET Core Runtime 10**, x64, to run the player and viewer. Check that both runtimes are installed when moving the public build to another PC.
- DiscChannelLab uses the user’s FFmpeg tools for disc playback. The viewer itself needs no FFmpeg installation or bundled codec.

## What the mixer changes

Use **Signal** to choose what is analyzed:

| Signal | Meaning |
| --- | --- |
| **Input channels** | Decoder PCM before the managed mixer. Mute, Solo and Master do not alter this view. |
| **Post-mix channels** (initial default) | Each channel’s combined left/right contribution after the active mix and Master. Faders, Mute and Solo are reflected. |
| **Final stereo output** | The exact clipped 2ch PCM passed to the player, including pan, summation, polarity cancellation and volume. |

Post-mix energy is calculated from `sample × sqrt(leftWeight² + rightWeight²) × masterGain`. It is a per-channel contribution, not a reconstructed speaker signal. Equal-power pan can change the left/right balance without changing that combined energy; a polarity sign alone also does not change an individual channel’s energy. Select **Final stereo output** to inspect those interactions. The display keeps original channel positions in Input/Post.

The managed multichannel path preserves the decoded channels. When the player has already converted a playback path to stereo, the viewer receives FL/FR; it cannot reconstruct original channels from that stereo signal. The viewer analyzes what is actually playing, so turn on the player’s mixer playback option to hear and visualize edited mixer settings. In the standard mix, LFE is muted by default; **Input channels** can still show its recorded content.

Both synchronization modes use moving particles emitted from the speakers. Past particles remain as a visual trail, not additional audio. Changing the mixer refreshes the look-ahead and clears incompatible listener-mode trails. The synthetic demo is independent of the player mixer.

## Appearance and saved settings

**Sync at** selects the reference point:

- **Listener (sound)** — default. Particles start at the speakers just as in Speakers mode, but emit early using actual decoded audio look-ahead. Each particle samples the spectrum at `playback time + travel time`, accounting for frequency, speed and horizontal spread. Its closest horizontal approach to the listener then coincides approximately with that sound. There are no particles held at the listener. LFE already starts at the listener’s feet and needs no travel offset.
- **Speakers (emission)** — the original behavior. Speakers emit on the beat; particles reach the listener later. Their arrival depends on frequency and visual speed.

**Particle speed** ranges from **0.5× to 4×**, with **2×** as the default relative to the original viewer. It changes visual motion only. Listener synchronization recalculates the emission lead at every speed. Both options are saved with the other viewer settings; older settings files use the new defaults. Changing synchronization, speed or spread clears incompatible listener-mode trails; playback continues normally.

Live look-ahead is bounded to about 8.2 seconds. It reads ahead from the existing decoder; the original PCM still goes through the live mixer at playback time. Playback never waits for a visualization buffer. Update **both the player and viewer**, then restart them. Immediately after playback starts, a seek, a track/segment change, or a mixer change, particles need time to travel inward. Missing look-ahead is not guessed or replaced by particles at the listener. Live mixer forecasts use the current controls; future user actions cannot be predicted, and the actual mixer’s short gain ramps can differ from the forecast. Hardware output latency is still not measured.

Both modes add smooth XYZ drift of at most about **2% of the speaker radius** per axis (about 7 cm at the current 3.5 m radius). Drift is stable while paused and fades to zero at emission. In Listener mode, a small smooth vibration (up to about 1.8 cm per axis at the center) remains around the synchronization point; the nominal trajectory and emission schedule are unchanged. The base height still represents frequency; LFE remains close to the floor. Lower bands have longer lifetimes, and higher bands receive stronger gentle distance attenuation **after passing the listener**. These are visual effects, not measured reverberation or air absorption.

**Listener emphasis:** only in **Listener (sound)**, particles gradually enlarge as they approach the listener, starting about **1.8 m** away and reaching the selected size within **25 cm**. **Listener size** adjusts this central multiplier from **1.0× to 3.0×**, default **1.5×** (previously 1.2×). It responds immediately, including while paused, and is saved with the display settings. At 1.0× there is no extra enlargement. **Particle size** continues to scale all particles. The separate **Listener brightness** slider adjusts central opacity gain from **1.0× to 4.0×**, default **1.4×**. Raising it also reduces the opacity of distant particles, so the listener stands out more clearly. The central gain falls off as a parabola with horizontal distance, reaching the dimmed surroundings at **60 cm**. At the maximum, distant particles retain about **36%** of their usual opacity; central opacity is capped at 100% for correct blending. At 1.0× the entire scene uses normal brightness. This responds immediately, including while paused, and is saved with the display settings. Frequency colors and the small vibration remain. These are existing moving particles; no stationary particles are added, and their arrival timing is unchanged. Speakers mode keeps its normal appearance.

Adjust Spread, Particle size (0.3–3×), Transparency (0–100%), Density and Persistence. Size, transparency and spread update existing particles immediately, including while paused. Transparency 100% hides particles; 0% uses the normal soft appearance. Floor guides, labels and LFE ripples have separate switches.

**Speaker beams** optionally adds spotlight-like beams from each non-LFE speaker to the listener. A soft, widening halo surrounds a thicker frequency-colored core; the existing moving particles and their small vibration remain visible. Each entire beam responds to the selected Signal's **current frequency-band energy at once**, so there is no speaker-to-listener visual travel delay. This is separate from the particle timing chosen by Sync at. Frequency height stays fixed with a thin vertical halo, and LFE keeps its floor particles and rings. Use **Beam strength** (0–2×, default 0.8×) to tune the effect; zero hides beams. Beams are off by default, and both controls are saved with display settings. Transparency and Listener brightness also affect the beams. The beams illustrate channel energy and direction, rather than measured acoustic propagation.

**Save settings / Ctrl+S** writes `ViewerSettings.json` beside the viewer EXE. It includes Signal, all display controls, demo volume, camera preset and exact angle/distance/pan, and the UI visibility state. Saved settings are restored on the next start; edits are saved only on request. To start next time with the UI hidden, hide it and press Ctrl+S. Settings are independent of disc metadata and mixer presets, and do not automatically start demo playback. Keep the viewer in a writable folder.

`AudioVisualizationSettings.json` contains the separate analysis settings. Restart the viewer after editing that file. A failed settings load leaves the current display usable; a failed save preserves the previously saved file.

## How to interpret the picture

This is an artistic visualization of channel spectra, **not a room-acoustics simulator or a sound-pressure measurement**. Particle color, height, speed, lifetime, size, spread, floor ripples and apparent lingering are visual choices. Their geometry does not reconstruct recorded source positions, real speaker dispersion, room reflections or acoustic phase interference. All LFE bands use the floor plane; other channels use frequency height rather than real speaker elevation.

The default analysis uses 48 kHz PCM, a 2048-point Hann window, a 512-sample hop, 16 logarithmic bands with 17 edges, 45 ms attack and 300 ms release. The low bands are limited by the FFT resolution. Rendering is capped at 24,000 particles. Live frames follow the player’s playback clock; hardware output latency is not measured. Frame rate and perceived synchronization vary with the PC and audio device.

## Local data and development checks

The link uses a bounded, one-way named pipe restricted to the current Windows user. The viewer serves its UI on `127.0.0.1`, with a per-launch token for API requests. Audio is not uploaded. Browser profile, endpoint token and logs are stored under `SpaceSketch/cache`; these, saved preferences, audio fixtures, binaries and downloaded tools are excluded from Git. Closing the window lets the local server shut down after its heartbeat timeout (about 75 seconds).

The [protocol description](visualizer-protocol.txt) documents Input/Post/Output, channel mapping, timing and queue behavior.

```powershell
# From the repository root, after ./build.ps1:
dotnet artifacts/public-win-x64/SpaceSketch/DiscChannelLab.Visualizer.dll --self-test
dotnet run --project DiscChannelLab.Visualizer.Verify -c Release -- --host artifacts/public-win-x64/SpaceSketch/DiscChannelLab.Visualizer.exe
dotnet run --project DiscChannelLab.Visualizer.Verify -c Release -- --write-demo verification-output/visualizer-demo.packet
node tests/visualizer-frontend.cjs
```

The frontend checks simulate browser and audio APIs. They verify controls and data flow, not actual GPU rendering or audible latency. The screenshots above were supplied from interactive use; a final visual and listening check on the target PC remains useful. This is a source preview; no binary release or ZIP is supplied.
