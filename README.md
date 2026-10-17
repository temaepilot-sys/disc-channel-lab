# DiscChannelLab

[日本語](README.ja.md)

DiscChannelLab is a Windows app that helps people explore multichannel audio. Its goal is to make the contents of a 5.1-channel recording, often a black box to nonexperts, easier to inspect and hear. You can listen to one channel at a time, export each channel to its own FLAC file, and compare adjustable stereo mixes.

The app reads supported, unprotected Blu-ray, DVD-Audio, and DVD-Video sources through .NET 10 and FFmpeg. It does not implement copy-protection removal or its own decoder for a branded audio format. It does not claim certification or affiliation with any audio-format owner.

## Screenshots

### Main window

![DiscChannelLab in English](docs/screenshots/english.png)

### Channel mixer

![Channel mixer in English](docs/screenshots/mixer-english.png)

Japanese screenshots: [Main window](docs/screenshots/japanese.png) · [Channel mixer](docs/screenshots/mixer-japanese.png)

## 3D visualizer — Space sketch

**The sound you hear becomes light you can see.**

Space sketch lets you follow which channels are active and the directions their sound flows from, through particles colored by frequency. With **Listener (sound)**, particles make their closest approach to the listener at approximately the moment the corresponding audio plays, bringing the channels' overlapping energy into focus.

Bass glows red and orange; treble shines blue and violet. Light gathers with the music, trembles, and flows back into the surrounding space, like sound taking the form of fireflies. We want to open the black box of multichannel audio: hear its details, discover them with your eyes, and enjoy simply watching the music unfold.

![Sound particles glowing together at the listener, with Listener sync and Input channels selected](docs/screenshots/listener-sound-focus.png)

*Channel energy across frequency bands, expressed as overlapping light at the listener.*

This companion window follows DiscChannelLab playback and lets you compare Input, Post-mix and final stereo output. Optional **Speaker beams** add soft spotlights with strong colored cores: each whole beam follows the current sound at once, without visual travel delay. It includes camera controls, a distraction-free view and saved JSON settings, with an English UI.

![Visualizer overview](docs/screenshots/visualizer.png)

![Listener view](docs/screenshots/listener.png)

[Usage, mixer behavior and requirements](docs/visualizer.md). Particle spread is an artistic representation, not a precise acoustic simulation.

## Headphones — turn toward the sound

Explore multichannel audio through headphones with **Direct** angle-based mixing or **Steam Audio HRTF**. Turn with the keyboard or opentrack; hear the channels change direction and watch the same movement in the 3D viewer. Front L/R separation is adjustable. Ordinary playback remains available.

![Headphones Lab with virtual speakers, HRTF model selection and reset controls](docs/screenshots/headphones.png)

The player always starts in **2ch Stereo**. Under **Preview Audio**, select **2ch Stereo**, **Headphones · Direct** or **Headphones · HRTF** while stopped or paused. Open **Headphones** for tuning; it no longer opens automatically. Keyboard mode keeps the listener fixed and rotates the room; tracking mode keeps the room fixed and turns the head. Headphone processing affects playback; existing FLAC exports use their source or stereo-mixer settings.

[Setup, controls, speaker placement, theory and limitations](docs/headphones.md).

Use the built-in HRTF or choose an **external SOFA** and replay the same marked passage to compare. Standard and SOFA retain separate comparison trims. Every Lab slider has an individual **↺** reset, and **Reset sound settings** restores the audio defaults together.

## Playback reliability

Pause and resume retain the native decoder and Windows audio device in Stereo, Direct and HRTF modes. Metering and visualization run separately from audio output; slow display updates are dropped instead of blocking sound. The viewer also reduces redundant FFT work and caps particle rendering at 30 fps. [Playback behavior and diagnostic checks](docs/headphones.md#playback-transitions).

## Features

- Inspect titles, chapters, tracks, audio streams, and channel layouts.
- Keep available chapters visible when audio is absent, unsupported, or cannot be analyzed. Titles show their inspection state; repeated menu clips are ranked below likely music titles.
- Recover Blu-ray audio by inspecting individual clips when playlist probing fails. Audio IDs are matched across clips even when stream indexes change; streams present in only part of a title are labeled.
- Preview audio with play/pause, previous/next track, seeking, and volume control.
- Recognize AC-3 and E-AC-3 audio on supported, unprotected Blu-ray sources as well as DVD-Video, using the external FFmpeg decoder. These lossy sources can be previewed, mixed, and exported at CD quality; FLAC conversion does not restore information lost in the source encoding.
- Open a separate channel mixer with vertical Input/Post meters, individual faders, numeric gain, Solo, Mute, Ø polarity inversion, and left/right pan. Optional fader links keep left/right pairs at the same gain. Multiple Solo channels can play together; Solo and Mute are mutually exclusive within each channel. Channel gain ranges from 0–100%; Master ranges from 0–200%. Changes update running audio without restarting playback.
- Export the channel mixer to stereo FLAC and save or load reusable JSON presets independently of disc and track metadata. See [Channel mixer](docs/channel-mixer.md).
- Preview volume uses a quadratic low-volume curve by default, with a linear-gain option for analysis. Both keep 100% at unity and 200% at twice the amplitude. Hover over the slider for actual gain and dB; changes ramp over 10 ms.
- See each input channel's peak level during playback, before stereo mixing and volume adjustment. The meters use 20 ms analysis windows and follow the audio player's clock. Hover over a meter to see its peak and RMS values in dBFS.
- Start in dark mode and English by default. Use the **Language** menu for Japanese and the **Dark mode** checkbox for a light theme; both choices are saved for the next launch.
- Select tracks or chapters and adjust split points in 0.1-second steps.
- Export multichannel FLAC, a configurable stereo mix, or one mono FLAC per source channel.
- Edit the disc name, title name, track names, and artists in the table or bulk editor. The title name is saved per disc and title.
- Save FLACs under `<output>/<disc name>/<title name>/<track>.flac`. Each FLAC uses the title name as its `ALBUM` tag and retains the disc name and chapter number in separate tags. Optional chapter folders go beneath the title folder; they are off by default.
- If a playlist extends slightly beyond the audio at a clip's end, conversion may add up to 20 ms of silence to preserve the requested sample count. Larger gaps still fail validation.
- Read saved Blu-ray track, chapter, and album edits from the earlier `Disc2Flac` app when the disc matches.
- Open a disc drive, a disc folder, or a mounted ISO. Protected sources are unsupported.

## A note from Claude Sonnet

> Even without a full 5.1 playback setup, listening to channels individually can reveal sounds that were recorded but have gone unheard.

An AI-generated recommendation supplied by the developer, translated from Japanese. [Read the full text](docs/claude-sonnet-note.md) · [Japanese text](docs/claude-sonnet-note.ja.md).

## Why can a 5.1 stereo mix sound quieter?

The standard mix uses fixed attenuation to leave room for channels being added together. In ordinary playback, initial channel-mixer settings and **Reset defaults** reproduce this same mix for the selected layout. This is a conservative starting point, with no loudness matching to a separate stereo version.

For 5.1 at Master 100%, using `S_L`/`S_R` for the left/right surround channels:

```text
a = 1/sqrt(2) ≈ 0.70710678
D = 1 + 2*a ≈ 2.41421356
L_out = (FL + a*FC + a*S_L) / D
R_out = (FR + a*FC + a*S_R) / D
LFE contribution = 0 by default
```

The relative `1 : 0.7071 : 0.7071` coefficients follow the stereo equations in [ITU-R BS.775-4, Annex 4, Table 2](https://www.itu.int/dms_pubrec/itu-r/rec/bs/R-REC-BS.775-4-202212-I!!PDF-E.pdf). The additional division is this app's fixed headroom policy. The same worst-case scaling rationale is described in the historical [ITU-R BS.1196 (1995), page 88](https://www.itu.int/dms_pubrec/itu-r/rec/bs/R-REC-BS.1196-0-199510-S!!PDF-E.pdf); this is a mathematical reference, not a claim of decoder compliance. [FFmpeg's pan documentation](https://ffmpeg.org/ffmpeg-filters.html#pan-1) also describes normalizing coefficient sums to one.

For normalized input samples with absolute values at most one, the triangle inequality gives `|L_out| <= (1 + a + a)/D = 1`. Thus the sum itself stays within full scale at default settings, even if contributing channels peak together with the same polarity. The scale factor is about `0.41421356`, or `−7.66 dB`, relative to the same unattenuated downmix. Front-only content is reduced by that amount. It is not a prediction of the loudness difference from a separately mastered stereo recording.

The equal-power pan controls express these coefficients as follows:

| 5.1 channel | Default fader | Pan | Contribution to output |
| --- | ---: | --- | --- |
| FL / FR | 41.42% | Left / Right | 0.4142 to its own side |
| FC | 41.42% | Center | 0.2929 to each side |
| Surround L / R | 29.29% | Left / Right | 0.2929 to its own side |
| LFE | 0% | Center | None |

The displayed percentages are rounded; internal coefficients retain precision. Setting FL/FR manually to 100% at their original pan positions still gives unity gain and equal Input/Post peaks. Other layouts use layout-dependent defaults; the five-channel reference above does not establish a universal standard for all 28 supported layouts.

LFE exclusion is an explicit default, not bass management; information present only in LFE is omitted. Channel correlation, phase cancellation, channel levels and differences in the source mixes affect perceived loudness and balance. This app does not interpret a disc's artistic downmix intent or automatically match loudness. Fixed scaling preserves dynamics but can leave unused headroom. A measured peak/loudness adjustment would require a separate analysis step and is not applied automatically.

Increasing faders, adding LFE or raising Master invalidates the default peak bound. Resampling and reconstructed true peaks also lie outside that simple sample-sum proof. There is no automatic limiter. Master is included in FLAC export only when explicitly checked. The playback mixer on/off choice now survives title/disc changes; when it is off, a notice identifies the standard mix, whose gains need not match edited fader positions.

## Reading and diagnostics

The **Reading and diagnostics** panel offers **Retry selected title**, **Inspect all titles**, and **Save diagnostic report**. Initial analysis shows chapter rows before audio probing finishes, then checks remaining titles in the background. Starting playback or another foreground operation stops the background scan. Retry uses longer, bounded probe timeouts. Reports include title states, original/effective chapter boundaries, clip information, errors, and FFmpeg versions; titles not yet inspected are marked accordingly. The report contains disc metadata and local diagnostic details, so review it before sharing.

A short video-only clip at the end of a Blu-ray title is rendered as silence while preserving the original timeline. Eligibility requires a clip of at most two seconds, a unique final occurrence, and an error-free full packet scan of a file no larger than 32 MB confirming video and no audio streams (including unsupported audio). The stream label and diagnostic report identify this treatment. Preview, seeking and FLAC export preserve the tail duration. Uninspected clips, failed probes, missing files, unsupported audio and longer/interior gaps are not silently filled; tracks with unresolved coverage remain unavailable. Standalone video-only titles still show **No audio**.

**Merge short final chapter** includes a final segment shorter than one second in the previous track. Uncheck it to restore the original boundary. The source markers remain intact, and boundary modes retain separate saved track layouts; metadata on matching track starts carries across when toggling. This option does not trim audio.

Audio correspondence uses transport IDs and compatible channel layouts, with unambiguous format/language matching when no ID exists. Ambiguous matches are rejected. Playback and CD export can handle a sample-rate change between matching clips; native high-resolution export requires matching rates and bit depths. A channel-layout change requiring a different interpretation is reported instead of silently changing channels. Short decode checks verify a sample, not every second of a title. Media damage, protected sources, and unsupported formats can still prevent playback.

Developer checks:

```powershell
dotnet run --project DiscChannelLab.Verify -c Release -- reading-tests
dotnet run --project DiscChannelLab.Verify -c Release -- diagnose H:\ diagnostics.json
```

The regression checks generate their own small clips and cover original boundaries, correction/edit persistence, menu ranking, timeouts/cancellation, stream-ID remapping and sample-rate changes, direct-clip fallback, failed-title isolation/retry, no-audio detection, and exact sample counts in cross-clip FLAC exports.

## SACD ISO input

**Open ISO** also accepts existing, readable SACD ISO files, read directly without mounting. Supply a Windows `sacd_extract.exe` compatible with the [EuFlo 0.3.9.x command line](https://github.com/EuFlo/sacd-ripper). Choose it with **SACD extraction tool**, put it in `tools` beside the app, or add it to `PATH`. It must support `-P`, `-2`, `-m`, `-s`, `-c`, `-t` and `-y`. No foobar2000 installation is needed.

- Stereo and multichannel areas appear as separate titles. Select the multichannel title to explore its channels. Names, performers and album/disc information are imported when present; existing editing and per-disc/area saved metadata work.
- The first track of a selected area is prepared to establish its actual format. Other tracks are extracted only when needed, with cancellation available. Completed caches are reused.
- DSD/DST is extracted to temporary DSF, then FFmpeg decodes/resamples it to **24-bit / 88.2 kHz PCM**, cached internally as FLAC. Channel layout comes from the extracted file; unknown/mismatched layouts are rejected. Existing playback, seek, mixer, meters, visualizer, stereo conversion and individual-channel export use this PCM.
- **CD quality** saves 16-bit / 44.1 kHz FLAC; **High resolution** uses the PCM intermediate. FLAC is lossless relative to that PCM. DSD-to-PCM conversion does not preserve the original DSD bitstream. Native DSD/DoP output and physical SACD extraction are outside this feature's scope.

The selected tool path is saved in `%LOCALAPPDATA%\DiscChannelLab\sacd-tool.json` and reused automatically after restarting the app. **SACD extraction tool** shows the current path in its tooltip and opens the file picker at that location when changing it. PCM caches use `sacd-cache` under the same app folder and consume disk space. Temporary files are confined to new job directories in that cache and removed on completion/cancellation. The application never writes to the input ISO.

The public build neither embeds nor downloads `sacd_extract`. The upstream source includes GPL licensing; retain its license and review its distribution conditions separately if distributing the executable. The adapter does not copy/link the extractor implementation. It uses file-mode commands and adds no copy-protection removal.

Developer check (use a new output folder per run):

```powershell
dotnet run --project DiscChannelLab.Verify -c Release -- sacd-tests verification-output/sacd-test
```

The check simulates the external tool's ISO/TOC interface and generates real multichannel DSF data, decoded through FFmpeg and the existing mixer/export paths; preview uses a dummy audio device. An actual SACD ISO and compatible extractor are still needed to verify image-specific compatibility and listening quality.

## Open audio files

Use **Open audio files** in the left panel to select one or more local files. Supported file types include FLAC, WAV/Wave64, AIFF, WavPack, APE, ALAC/M4A, AAC, Ogg/Vorbis, Opus, MP3, WMA, CAF and Matroska audio, subject to the external FFmpeg build's decoders. File input uses the existing player, channel mixer, meters, 3D visualizer, stereo mix export, individual-channel export and multichannel FLAC export.

Compatible files in the same folder and album are grouped into one track list, ordered by embedded disc/track numbers when present. Different codecs, rates, depths or speaker layouts form separate titles. Embedded titles, artists and chapter markers are read; containers with multiple audio streams expose a stream selector. Saved edits belong to the selected file collection. Optical-drive polling leaves the selected files in place.

Mono and stereo are supported alongside the existing recognized multichannel layouts up to eight channels. A multichannel file must identify its speaker layout: no channel order is inferred from its channel count. Opening files and editing track information do not modify the originals or create an intermediate full-album audio cache. Files changed or moved after opening must be reopened.

CD-quality FLAC uses 16-bit/44.1 kHz. **Save as Multichannel FLAC** also offers **Original PCM quality** for integer 16/24-bit input at 44.1 kHz or higher, preserving the channel count, speaker layout and complete-file PCM samples. Lossy or floating-point decoding precision is marked `PCM` and is not advertised as original integer bit depth; CD-quality export remains available for these sources at 44.1 kHz or higher. Direct DSD files and layouts beyond the supported set are outside this input path.

### FLAC sample compatibility

All seven files in [sfiera/flac-test-files](https://github.com/sfiera/flac-test-files/tree/9f9caf867883aa7066b74f64c82185364fd070ef) passed input, playback through a dummy audio device, visualizer PCM delivery and CD-quality stereo export checks:

| File | Channels | Detected layout |
| --- | ---: | --- |
| stereo.flac | 2 | stereo |
| center.flac | 3 | 3.0 |
| quad.flac | 4 | quad |
| surround50.flac | 5 | 5.0(side) |
| surround51.flac | 6 | 5.1(side) |
| surround61.flac | 7 | 6.1 |
| surround71.flac | 8 | 7.1 |

For the six multichannel samples, original 16-bit/48 kHz export preserves every PCM sample. Every individually exported channel matches an independent CD-quality reference within two 16-bit LSBs of dither. The 5.0/5.1 pair called rear in the sample README is labeled SL/SR by FFmpeg; [FLAC's channel definition](https://www.rfc-editor.org/rfc/rfc9639.html#section-9.1.3) calls this pair back/surround. Channel slots are preserved. This checks the software signal path, not physical speaker wiring or subjective listening. The external GPLv2 sample recordings are not bundled with the app.

To repeat the check with a separate checkout of the samples:

```powershell
dotnet run --project DiscChannelLab.Verify -- flac-sample-tests C:\test\flac-test-files C:\test\results
```

## Cache size and cleanup

Use **Cache control** at the bottom of the left panel to open a separate window showing the combined size of **SACD audio caches** and the linked **viewer cache**. Size is measured at startup and again when the window opens. Use **Refresh size** to measure it again and **Clear cache** to remove these generated files. The window leaves the player controls available. Clearing is available after playback, conversion and title inspection finish, and after other players and the viewer close. Inaccessible files are reported. A cleared SACD track is extracted and decoded again when needed.

Track/album edits, mixer presets, viewer settings, exported FLAC files, logs and the required FFmpeg tools are kept. Tool storage is shown separately from the clearable total. Cache traversal refuses filesystem links. There is no automatic expiry or size limit.

## Multichannel FLAC export

Select **Save as Multichannel FLAC**, then choose the quality on the left:

- **CD quality**: 16-bit / 44.1 kHz per channel, keeping the channel count and layout. This is a multichannel FLAC, not an audio CD.
- **Original PCM quality**: retain the sample rate and 16-bit or 24-bit PCM precision without applying the stereo mixer or master volume. For SACD, this means the already decoded **24-bit / 88.2 kHz PCM**, with audio samples matching the cache. It does not represent a native DSD bitstream.

Existing selected-track export, edited boundaries, tags and disc/title folder organization apply. This mode excludes stereo-mixer export and separate mono-channel export; existing files are not overwritten.

## Requirements

- Windows x64.
- .NET 10 SDK to build; .NET 10 Desktop Runtime to run the public player. The viewer also needs ASP.NET Core Runtime 10 (x64) and a supported Firefox- or Chromium-based browser with WebGL. Choose a detected browser when opening the viewer; it uses a separate viewer profile.
- An FFmpeg installation supplied by the user. `ffmpeg.exe` and `ffprobe.exe` are required; `ffplay.exe` is required for ordinary preview playback. Direct/HRTF headphone playback uses Windows audio output. Place them in a `tools` folder beside the app or make them available on `PATH`.

For supported multichannel stereo playback, Input/Post/Output meters are measured together from PCM in the app. Other playback paths use FFmpeg's `astats` and `ametadata` filters; those meters remain unavailable if the filters are missing.

The FFmpeg build must provide the demuxers, protocols, and decoders needed for your discs. DVD-Audio LPCM requires the `pcm_dvda` decoder; check with `ffmpeg -hide_banner -decoders`. Older builds can misidentify this audio and fail to play it. In particular, FFmpeg's [DVD-Video demuxer](https://ffmpeg.org/ffmpeg-formats.html#dvdvideo) requires GPL library support and `libdvdnav`/`libdvdread`. Consult [FFmpeg's licensing guidance](https://ffmpeg.org/legal.html) for the FFmpeg build you choose. This repository does not contain or download FFmpeg binaries.

## Build

Prepare the pinned Steam Audio runtime and its notices first (Python 3 required):

```powershell
python fetch-steamaudio.py
```

Then, from PowerShell in this directory:

```powershell
./build.ps1
```

The output is in `artifacts/public-win-x64`, with the separate viewer in its `SpaceSketch` subfolder. Click **3D Viewer** in the player to open it. It is a framework-dependent, multi-file application. Run `DiscChannelLab.exe` after installing the .NET 10 Desktop Runtime and making the FFmpeg tools available. The public build does not embed FFmpeg or include a single-file executable.

For personal use, `./build-personal.ps1` builds a self-contained application folder from **the same source files**. It embeds the FFmpeg executables installed on the builder's computer. Keep the complete folder, including `tools` and `SpaceSketch`. Its output is local and is excluded from this repository; it is not the public distribution build. Before distributing it, review the licenses and corresponding-source obligations of every included component.

## Settings and migration

The player stores language (`language.txt`), theme (`theme-v2.txt`) and disc/track edits (`track-edits`) under `%LOCALAPPDATA%\DiscChannelLab`. New logs and the personal build’s extracted tool cache also use this folder; temporary fallbacks use `%TEMP%\DiscChannelLab`.

On startup, existing language/theme files and per-disc JSON metadata are copied from `%LOCALAPPDATA%\BDDVD2Flac` only when the destination file is missing. This preserves disc/title/chapter names, artists, selections, edited split points, both boundary modes and reading options. Current DiscChannelLab files always take precedence. Old files remain unchanged so an earlier build can still read them. Logs and tool caches are not imported; embedded tools are extracted again as needed.

If a copy fails, the app retains read access to legacy settings and retries missing files on a later launch. Older `Disc2Flac` Blu-ray metadata remains readable. Mixer preset files keep their chosen locations, and the separate viewer still stores `ViewerSettings.json` beside its EXE.

A developer regression check uses isolated fixtures rather than the real user profile:

```powershell
dotnet run --project DiscChannelLab.Verify -c Release -- settings-migration-test verification-output/settings-migration
```

## Development and publication status

`DiscChannelLab.App` is the shared application project for both build modes. Feature changes belong there once, so the two builds stay in sync. User settings and saved track edits use `%LOCALAPPDATA%\DiscChannelLab`; startup imports missing settings from the former `BDDVD2Flac` folder without overwriting current edits. See [Settings and migration](#settings-and-migration). The original application source is offered under [Apache License 2.0](LICENSE); external tools remain under their own licenses. This repository is a development preview of the source code, with no binary release. Source-provenance and dependency review remain open before a tagged release. See [PUBLICATION_CHECKLIST.md](PUBLICATION_CHECKLIST.md).
