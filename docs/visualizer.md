# Space sketch — 3D audio visualizer

[日本語](visualizer.ja.md) · [Back to DiscChannelLab](../README.md)

Space sketch is an experimental companion viewer for DiscChannelLab. It turns each channel’s frequency-band energy into colored particles, helping you explore how multichannel recordings fill a listening space. It also works as a place to simply watch music unfold.

## Rendering modes

Both build editions offer **Particles**, **Spark Field**, **Hybrid · Spectral Veil**, and **Aurora + Spark Field**. The personal and public builds share the same source and features. Electric Flow and the earlier standalone Aurora are retired.

**Spark Field** combines short micro sparks, tiny streaks, and lingering soft motes, driven by current audio. [Implementation notes](spark-field-implementation-notes.md).

**Aurora + Spark Field** overlays the current band-reactive veil with Spark Field. Choose **Veil colors → Original · Spectrum** for the original veil palette, or **Match Spark Field** to share Spark's exact frequency-to-color mapping. Spark colors remain unchanged; hot spark cores can still appear white. Calm and Warm veil palettes are also available. Both sets of controls, vertical flow, Mute/Solo and Save settings apply in the combined mode. Only the current FFT is used, with no extra decoding or look-ahead. The combined view is capped at 30 fps by default, or 60 fps when both quality settings are High. The old particle pool and speaker beams stay idle in this mode.

**Hybrid · Spectral Veil** combines the original particles with independently animated frequency bands around each actual speaker. All 16 current FFT bands are retained: bass is low, treble high. Each band's fast energy controls its own vertical/depth displacement and brightness; positive spectral changes excite a stronger response. Quiet bands settle, rather than following a common clock-driven wave. LFE forms a low circular field at the listener’s feet when present and enabled.

**Vertical flow** adds soft falling light (**Downward · Waterfall**) or rising light (**Upward**) over that motion. **Off** retains the previous appearance. The color/frequency height stays fixed; light accents travel through the active bands. Each source drives its own flow from audio time and energy, and pause freezes it. LFE stays on the floor. The direction is included in Save settings.

Reopening Space sketch restores the current view and drawing mode from the running companion's memory. **Save settings** is still needed to restore that view after the companion exits; **Load settings** reads the saved file. A bounded in-memory PCM window (384 packets / at most 24 MiB) lets Listener-synchronized Particles resume without waiting for the player's entire look-ahead window. Unused future FFTs remain disabled in Spark Field, Hybrid and Aurora + Spark Field.

- **Veil colors** appears directly below Visual style. **Veil settings**: Vibration rate, Band movement, Intensity, Opacity, Density, Afterglow, Quality, and Hybrid particle amount in Hybrid only. Gentle / Clear response / Percussive are presets. Settings use the existing saved preferences; old Aurora/Electric mode selections fall back to Particles.
- Pause freezes the bands. Seek, source/signal changes and disconnect clear stale light. Mute/Solo immediately clears disabled sources. Final stereo shows FL/FR only. Camera/head orientation uses the existing shared scene transform.
- Hybrid follows current audio; particle travel synchronization remains available in Particles. No new FFT, decoding, audio delay, or audio thread work is introduced.
- The motion is an **illustration of spectral activity**, with vibration slowed for visibility. Magnitude-only FFT data cannot reconstruct acoustic phase or measured room pressure. Afterglow is visual, not recorded reverberation.

Indexed GPU meshes use two persistent buffers: Low 24×32 cells / one layer, Medium 40×48 / two layers, High 64×64 / two layers. Low/Medium cap rendering at 30 fps; High at 60 fps. Resources are allocated only when the mode is selected.

Verification includes actual WebGL screenshots, synthetic per-band isolation, real multichannel PCM pipe + FFT, Input/Post/Output, Mute/Solo, pause and seek. Use `node tests/hybrid-checks.cjs` for band-motion logic. [Implementation and verification notes](spark-field-implementation-notes.md) describe the measurements and their limits.

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

- Windows x64 and a supported Firefox- or Chromium-based browser with WebGL support.
- .NET 10 SDK to build. The public output is framework-dependent: install both the **.NET 10 Desktop Runtime** and **ASP.NET Core Runtime 10**, x64, to run the player and viewer. Check that both runtimes are installed when moving the public build to another PC.
- DiscChannelLab uses the user’s FFmpeg tools for disc playback. The viewer itself needs no FFmpeg installation or bundled codec.

Closing the 3D browser window does not always stop its local host immediately. Pressing the player’s 3D button now asks that existing host to show the viewer again, or activates its existing window. If the host has already exited, the button starts a new one. Install the matching updated player and viewer together.

## Browser choice

Opening **3D Viewer** detects installed browsers and shows a chooser. Nothing is selected automatically on first use. Your last explicit choice is remembered in `SpaceSketch/BrowserSettings.json`, and the chooser appears again on the next viewer launch. **Detect again** rescans installations; **Browse…** can select the actual browser executable from a portable installation. If the saved browser is missing, choose another yourself; the viewer never silently falls back to Edge, Chrome or the system default. Cancel closes the launcher without opening a browser.

Firefox, LibreWolf, Waterfox, Floorp and Zen use a separate Firefox-style viewer profile. Edge, Chrome, Chromium, Brave, Vivaldi and Opera use a separate Chromium-style viewer profile. Profiles live under `SpaceSketch/cache/browser-profiles`, separate for each browser installation. The viewer does not open your normal signed-in browser profile or change the Windows default browser. Firefox account services are disabled in its viewer profile; Chromium is started with sync disabled. Startup welcome/default-browser prompts and background services are suppressed where supported. Browser-specific behavior and policies can change; this is not a network firewall or a guarantee that the browser makes no background requests.

The launch parameters follow [Firefox's profile/instance documentation](https://firefox-source-docs.mozilla.org/browser/CommandLineParameters.html) and [Chromium's sync switch](https://chromium.googlesource.com/chromium/src/+/HEAD/components/sync/base/command_line_switches.h). Browser selection does not change the player's audio engine or the local PCM link.

## What the mixer changes

### Speaker switches

Lab speaker circles and mixer Mute share the same channel state. In the 3D viewer, muted or Solo-excluded speakers and labels become dark. **Input channels and Post-mix channels** both stop their particles, beams and LFE field immediately, including existing particles and old look-ahead, in either synchronization mode and while paused. Unmuting resumes emission during playback without reopening the viewer. Input PCM and spectra retain their original values; the speaker switches control visibility. **Final stereo output** shows the mixed left/right outputs: muting an FL input does not hide other channels still contributing to that left output. Speaker state changes preserve the camera and other channels' particles.

Use **Signal** to choose what is analyzed:

| Signal | Meaning |
| --- | --- |
| **Input channels** | Decoder PCM before the managed mixer. Original levels are retained; speaker Mute / Solo controls which channels are visible. |
| **Post-mix channels** (initial default) | Each channel’s combined left/right contribution after the active mix and Master. Faders, Mute and Solo are reflected. |
| **Final stereo output** | The exact clipped 2ch PCM passed to the player, including pan, summation, polarity cancellation and volume. |

Post-mix energy is calculated from `sample × sqrt(leftWeight² + rightWeight²) × masterGain`. It is a per-channel contribution, not a reconstructed speaker signal. Equal-power pan can change the left/right balance without changing that combined energy; a polarity sign alone also does not change an individual channel’s energy. Select **Final stereo output** to inspect those interactions. The display keeps original channel positions in Input/Post.

The managed multichannel path preserves the decoded channels. When the player has already converted a playback path to stereo, the viewer receives FL/FR; it cannot reconstruct original channels from that stereo signal. Enable the player's mixer playback option to hear and visualize edited mixer settings. In the standard mix, LFE contributes no output by default; Input retains its original spectrum, while an explicit Mute / Solo exclusion or zero mixer fader also hides that channel's particles.

Both synchronization modes use moving particles emitted from the speakers. Past particles remain as a visual trail, not additional audio. Changing the mixer refreshes the look-ahead and clears incompatible listener-mode trails. The synthetic demo is independent of the player mixer.

## Move through the 3D space

Click the scene to use **W/S** forward/back along the viewing direction, **A/D** strafe left/right, **Q/E** turn left/right in place, and **Z/X** move down/up in world height. Hold **Shift** for 4× movement/turning or **Alt** for 0.15× precision. Alt takes priority if both are held. Release the keys to stop; losing focus also cancels movement.

The first movement switches to **Camera → Free movement** without moving the camera to another preset. Arrow keys and mouse dragging adjust the view; in Free movement they turn at the current position. **Home** or **Reset camera** restores the selected camera mode's default position and forward view, keeping that mode selected. In Free movement, it returns to the defaults of the preset you departed from while keeping Free movement selected. **Save settings** remembers the flight position, heading and Home preset. Navigation works while paused and with every drawing mode. It controls the picture only; headphone audio direction and playback are unchanged. Keyboard controls do not take over input fields or Ctrl/Command shortcuts.

## Appearance and saved settings

**Sync at** selects the reference point:

- **Listener (sound)** — default. Particles start at the speakers just as in Speakers mode, but emit early using actual decoded audio look-ahead. Each particle samples the spectrum at `playback time + travel time`, accounting for frequency, speed and horizontal spread. Its closest horizontal approach to the listener then coincides approximately with that sound. There are no particles held at the listener. LFE already starts at the listener’s feet and needs no travel offset.
- **Speakers (emission)** — the original behavior. Speakers emit on the beat; particles reach the listener later. Their arrival depends on frequency and visual speed.

**Particle speed** ranges from **0.5× to 4×**, with **2×** as the default relative to the original viewer. It changes visual motion only. Listener synchronization recalculates the emission lead at every speed. Both options are saved with the other viewer settings; older settings files use the new defaults. Changing synchronization, speed or spread clears incompatible listener-mode trails; playback continues normally.

Live look-ahead is bounded to about 8.2 seconds. It reads ahead from the existing decoder; the original PCM still goes through the live mixer at playback time. Playback never waits for a visualization buffer. Update **both the player and viewer**, then restart them. Immediately after playback starts, a seek, a track/segment change, or a mixer change, particles need time to travel inward. Missing look-ahead is not guessed or replaced by particles at the listener. Live mixer forecasts use the current controls; future user actions cannot be predicted, and the actual mixer’s short gain ramps can differ from the forecast. Hardware output latency is still not measured.

Both modes add smooth XYZ drift of at most about **2% of the speaker radius** per axis (about 7 cm at the current 3.5 m radius). Drift is stable while paused and fades to zero at emission. In Listener mode, a small smooth vibration (up to about 1.8 cm per axis at the center) remains around the synchronization point; the nominal trajectory and emission schedule are unchanged. The base height still represents frequency; LFE remains close to the floor. Lower bands have longer lifetimes, and higher bands receive stronger gentle distance attenuation **after passing the listener**. These are visual effects, not measured reverberation or air absorption.

**Listener emphasis:** only in **Listener (sound)**, particles gradually enlarge as they approach the listener, starting about **1.8 m** away and reaching the selected size within **25 cm**. **Listener size** adjusts this central multiplier from **1.0× to 3.0×**, default **1.5×** (previously 1.2×). It responds immediately, including while paused, and is saved with the display settings. At 1.0× there is no extra enlargement. **Particle size** continues to scale all particles. The separate **Listener brightness** slider adjusts central opacity gain from **1.0× to 4.0×**, default **1.4×**. Raising it also reduces the opacity of distant particles, so the listener stands out more clearly. The central gain falls off as a parabola with horizontal distance, reaching the dimmed surroundings at **60 cm**. At the maximum, distant particles retain about **36%** of their usual opacity; central opacity is capped at 100% for correct blending. At 1.0× there is no extra spatial contrast. This responds immediately, including while paused, and is saved with the display settings. Frequency colors and the small vibration remain. These are existing moving particles; no stationary particles are added, and their arrival timing is unchanged. Speakers mode keeps its normal appearance.

Adjust Spread, Particle size (0.3–3×), Transparency (0–100%), Density and Persistence. Size, transparency and spread update existing particles immediately, including while paused. Transparency 100% hides particles; 0% uses the normal soft appearance. Floor guides, labels and LFE ripples have separate switches.

**Fast listener light:** in Listener (sound), the brightness of particles within the central 60 cm follows the **current energy of their own channel and frequency band**, including particles already in flight. A separate unsmoothed FFT frame avoids the long release used for particle emission. The light envelope has a 12 ms attack and a 60 ms release after the current analysis arrives. In silence it keeps only a faint 4% glow at the center. Particle paths, small vibration, size and lifetime continue independently. Speaker beams use the same fast levels. Pausing freezes the light as well. Input, Post and Output retain their signal meanings; choose **Final stereo output** to include player volume and the final mix. These times are envelope settings, not a guarantee of end-to-end hardware latency: the FFT window, 50 ms polling and rendering also contribute.

Beam visibility is tuned to keep quiet active bands readable: a gentler intensity curve, a wider colored core and a stronger soft halo preserve the spotlight shape. The particle contrast setting cannot reduce the beam's spatial multiplier below 0.65, so the direction remains visible even when the surroundings are dimmed. Silence still produces no beam, and the fast response is preserved.

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

Run `./tests/verify-space-sketch.ps1` for the self-tests, real pipe/host tests and frontend checks. Add `-Browser` for actual Chrome/WebGL checks of Home, rendering, color matching, settings and player-linked audio. Browser checks require Chrome and Node.js 22 or newer; ordinary playback does not require either Node.js or a particular browser brand. Set `LAB_VIEWER_EXE` when running an individual browser script against another build. Local results are saved under `verification-output/space-sketch`.

Frontend mocks verify controls and data flow; the browser checks separately verify actual WebGL. Audio queue tests use a test output sink, so they do not establish acoustic latency or listening quality on every device. See the [update summary](changes-2026-10-18.md). GitHub contains source and documentation; no binary release or ZIP is included with this update.

## Headphone direction link

[Headphone playback](headphones.md) sends its actual pose and speaker geometry to the viewer. Keyboard control keeps the listener fixed and rotates the room; opentrack keeps the room fixed and turns the head. Headphone mode uses current speaker emission sync, with zero-travel-delay beams. Ordinary playback retains its Listener/Speakers synchronization choice.
