# Headphone playback

[日本語](headphones.ja.md)

Turn toward a virtual speaker and hear its channel move with you. **Headphones** brings the approved Direct and Steam Audio renderers into the main DiscChannelLab application, alongside ordinary stereo playback.

## Getting started

1. Open a disc, ISO or audio file in the main window and choose a track.
2. Under **Preview Audio**, select **2ch Stereo**, **Headphones · Direct** or **Headphones · HRTF**. Change renderer while stopped or paused. The adjacent selector still chooses all channels or an individual source channel. Open **Headphones** for tuning controls; its enable checkbox and renderer stay synchronized with Preview Audio.
3. Choose **Direct** or **Steam Audio**, then play using headphones. Both use the same source and speaker geometry.
4. Click the room diagram and use the keys below, or enable opentrack. Open **3D Viewer** to see the same orientation and placement.

Every launch starts in **2ch Stereo**, even when the previous session used headphones. The headphones window opens only when requested. Headphone tuning and the last Direct/HRTF choice are retained; an existing personal Headphones Lab profile is imported once without enabling headphone playback.

| Renderer | Processing |
| --- | --- |
| Direct | Equal-power pan from relative direction, a spherical-head shadow filter, and adjustable rear high-frequency reduction. Approximate interaural delay is optional. |
| Steam Audio | Steam Audio 4.8.1 applies its built-in HRTF separately to each source channel, with direction interpolation. Direct filters and additional delay are bypassed. |

Neither renderer adds room reflections, wall occlusion or distance attenuation. LFE is added equally to both ears at 70% by default, without directional filtering. A 5.0 input does not acquire an LFE channel.

## Speaker placement and L/R separation

| Layout | Front L/R | Surround pair | Separate back pair |
| --- | --- | --- | --- |
| Stereo | ±30° | — | — |
| 5.0 / 5.1 | ±30° | ±110° | — |
| 7.0 / 7.1 with side and back pairs | ±30° | ±110° | ±150° |

The front and 5.1 surround angles follow [ITU-R BS.775-4](https://www.itu.int/dms_pubrec/itu-r/rec/bs/R-REC-BS.775-4-202212-I!!PDF-E.pdf). The 7.1 side/back positions fall within [ITU-R BS.2051-3, Table 11](https://www.itu.int/dms_pubrec/itu-r/rec/bs/R-REC-BS.2051-3-202205-I!!PDF-E.pdf). Other supported layouts have illustrative placements; this is not a claim of certification. LFE's central floor position in the viewer is artistic.

A lone BL/BR surround pair uses ±110°, just like SL/SR in a 5.1 layout. BL/BR uses ±150° when a separate SL/SR pair exists. Channel order comes from the explicit input layout, never channel count alone.

**Front L/R angle** adjusts FL/FR to ±15–90° during playback. Wider angles make L/R easier to distinguish. Direct approaches one-ear routing at ±90°; HRTF retains the contribution arriving at the opposite ear. **Standard ±30°** restores the recommended front placement. Both renderers, the room diagram and the viewer use the same setting.

Click an orange speaker circle or its channel label in the Lab room diagram to **Mute / unmute** that channel, including LFE. Muted speakers become dark with a diagonal line; other channels suppressed by Solo are dimmed. The switch shares the active mixer's Mute state and automatically enables that mixer if bypassed. It works in Stereo, Direct and HRTF without changing the playback mode. Muting clears that channel's Solo, without changing other channels' Solo or linked fader levels. Speaker switches require a loaded, supported layout. The room remains focused for arrow-key controls.

## Keyboard and opentrack

Click the room diagram first. Keyboard/manual operation keeps the listener facing forward and rotates the surroundings in the opposite direction.

| Key | Action |
| --- | --- |
| ← / → | Yaw left/right |
| ↑ / ↓ | Pitch up/down |
| Q / E | Roll |
| Shift + direction key | 1° steps instead of 5° |
| Home / Center | Reset forward, or center the tracker |

For opentrack, select **UDP over network**, address **127.0.0.1**, port **4242**. Use 1:1 yaw/pitch/roll output curves. Enable **Receive opentrack UDP**, look forward and press **Center**. Invert axes if necessary, then center again. This mode keeps the room fixed and turns the head.

The receiver accepts the [opentrack UDP packet](https://github.com/opentrack/opentrack/blob/master/proto-udp/ftnoir_protocol_ftn.cpp): six little-endian doubles, x/y/z/yaw/pitch/roll. Position is ignored. Only local loopback is bound. After 0.5 seconds without data, the last pose is held and the status reports lost tracking. TrackIR must connect through opentrack; there is no direct TrackIR SDK integration.

## Mixer, comparison and output

Gain, Mute, multi-Solo, polarity inversion and fader links apply before spatialization. Headphone faders start at **100% source gain**; common output headroom starts at **−12 dB**. Ordinary playback retains its conservative standard-downmix defaults. Each mode remembers its own fader bank and mixer enable choice during the session. Reset defaults applies to the active bank.

Headphone left/right routing comes from speaker direction. The mixer's Pan controls continue to affect stereo FLAC export. **Headphone rendering affects playback only**: existing source, multichannel, separate-channel and stereo-mixer FLAC exports retain their established processing. HRTF is not written into exported FLACs.

For an A/B comparison, use **Mark position**, pause, change renderer, then **Play from mark**. The mark belongs to the current track. Match perceived loudness with the two comparison trims; there is no automatic loudness matching.

Audio is decoded to 48 kHz float PCM and rendered in 256-sample blocks to Windows' default shared-mode output device. A bounded queue holds roughly 171–341 ms of unprocessed PCM. SACD extraction and startup prebuffering complete before the output device opens. DSP and Windows output run together on one dedicated MMCSS audio thread, and the approximately 30 ms device buffer is primed before playback starts. Read-ahead does not pre-render head direction, faders or volume; they remain applied immediately before output. The diagnostic **empty buffer events** counter is separate from clipping and helps identify scheduling/underrun problems. Pose is sampled after waiting for device space; orientation smoothing defaults to 8 ms. The reported device buffer is not total measured end-to-end latency. No automatic normalization or limiter is added. Final finite-value protection and clipping to ±1 are applied, and clipped samples are counted. Reduce Master or increase headroom if the count rises.

## Playback transitions

Playback starts, seeks and resumes use an 8 ms raised-cosine fade from silence, after headphone rendering. Stopping releases the last submitted stereo sample smoothly to zero before the device closes; it does not replay queued decoder blocks. Shutdown has a timeout, and the Windows endpoint buffer is explicitly reset on close. These short transport fades prevent abrupt waveform cuts; they do not alter stored audio, FLAC exports or steady playback levels.

Direct and HRTF also run a short **silent device guard before the first source frame and after the final release**. The guard is at least 64 ms, extends to cover the endpoint buffer plus Windows' reported stream latency, and is capped at about 500 ms. Silent packets use Windows' `AUDCLNT_BUFFERFLAGS_SILENT`; the client is reset on opening and closing. This adds a small delay to transport changes so the downstream output path can settle to silence. Guards do not count as track time and do not offset meters or the 3D viewer. Preparation can be canceled without playing a track fragment. This addresses possible device transition noise; a clean digital test alone cannot establish that every device is acoustically free of clicks.

`headphone-restart-tests <output-folder>` checks repeated loud-to-silent renderer replacement, consecutive 5.1 FLAC tracks, guards at different device latencies, cancellation and source-clock alignment. Together with `headphone-buffer-tests`, it verifies the PCM and silent Windows output path without playing audible test tones.

## Viewer synchronization

In keyboard mode, speakers, grid, labels, particles and beams rotate around the fixed listener. In tracker mode, the room stays fixed; **Listener** camera can follow the head. L/R width and surround placement match the audio renderer.

Headphone playback uses **Speakers (emission)** synchronization: future head motion cannot be known for the normal viewer's look-ahead calculation. Beams show current audio; particle travel remains an artistic effect. Ordinary playback retains its Listener/Speakers synchronization choice.

Input shows source channels; Post shows each source's summed contribution to the two ears; Final stereo shows the rendered output. Post can decrease through left/right phase cancellation and is not an acoustic energy measurement at the ears.

## Model and limitations

The Direct head-shadow filter uses the spherical-head model in [Brown & Duda (1998), equations 3–5](https://users.umiacs.umd.edu/~ramanid/cmsc828d_audio/BrownDuda.pdf). Optional delay uses the Woodworth approximation, about 0.656 ms at 90° with the default 8.75 cm head radius. The separate rear shelf is a listening approximation: `max(0, -relativeZ)^2` weights a default −6 dB high-frequency shelf at 4 kHz. It is not a universal rule for sound behind a listener, and does not reproduce measured outer-ear cues.

Steam Audio uses its [binaural effect](https://valvesoftware.github.io/steam-audio/doc/capi/binaural-effect.html) and default HRTF. Localization and front/back or height perception vary with the listener. This HRTF is not personalized. Physical tracker wearing tests and individual listening calibration remain user checks; synthetic UDP tests do not establish those results.

## Settings and build

Settings use `%LOCALAPPDATA%/DiscChannelLab/headphones.json`, separate from disc metadata and mixer presets. Save explicitly or close the window/application. Pose and comparison marks are not persisted. A one-time import reads `DiscChannelLab.HeadphonesLab`; newer trial metadata is copied, replaced main metadata is backed up in `DiscChannelLab/imports/headphones-lab-backup`, and caches are not copied.

Run `python fetch-steamaudio.py` before the public or personal build script. It downloads the official pinned 4.8.1 Windows x64 runtime and checks the recorded package SHA256. This hash is a development pin, not a vendor signature. Keep `tools/steamaudio/LICENSE.txt`, `THIRDPARTY.md` and provenance with the runtime. The repository tracks notices, not runtime binaries. Direct and ordinary playback can be compiled without the native runtime using `dotnet build`; HRTF then requires the runtime to be supplied.

Both build scripts use the same source. Public output excludes FFmpeg and sacd_extract; the personal output includes the local FFmpeg bundle and .NET runtime. Keep the complete output folder, including `tools` and `SpaceSketch`.

Developer checks: `headphone-tests`, `headphone-ui-tests`, `headphone-buffer-tests`, and `settings-migration-test` in `DiscChannelLab.Verify`. They cover DSP, actual HRTF calls, decoding and cancellation, UDP, silent Windows audio output, language switching and the normal/headphone fader banks. Set `DISCCHANNEL_DATA_HOME` to a temporary parent directory for isolated tests.

Run from the repository root after supplying the seven external sfiera samples under `verification-output/flac-fixtures-sfiera`. Run the DSP check before the UI check, which uses its generated six-channel fixture:

```powershell
dotnet run --project DiscChannelLab.Verify -c Release -- headphone-tests verification-output/headphones/dsp-final
dotnet run --project DiscChannelLab.Verify -c Release -- headphone-ui-tests verification-output/headphones/ui
```

`headphone-buffer-tests` checks bounded startup buffering, cancellation and a failed output device, and measures actual Windows buffer continuity while processing multichannel FLAC through both renderers. Its Windows output receives silence; it can optionally analyze an existing audio file without modifying it.

`headphone-click-tests <output-folder>` records rendered PCM to verify smooth starts and stops, cancellation while priming, fresh state after restart, bounded shutdown, and FLAC seeks through Direct and HRTF. It does not send audible sound to the device.
