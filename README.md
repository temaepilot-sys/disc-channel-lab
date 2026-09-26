# DiscChannelLab

[日本語](README.ja.md)

DiscChannelLab is a Windows app that helps people explore multichannel audio. Its goal is to make the contents of a 5.1-channel recording, often a black box to nonexperts, easier to inspect and hear. You can listen to one channel at a time, export each channel to its own FLAC file, and compare adjustable stereo mixes.

The app reads supported, unprotected Blu-ray, DVD-Audio, and DVD-Video sources through .NET 10 and FFmpeg. It does not implement copy-protection removal or its own decoder for a branded audio format. It does not claim certification or affiliation with any audio-format owner.

## Features

- Inspect titles, chapters, tracks, audio streams, and channel layouts.
- Preview audio with play/pause, previous/next track, seeking, and volume control.
- Preview volume uses a quadratic low-volume curve by default, with a linear-gain option for analysis. Both keep 100% at unity and 200% at twice the amplitude. Hover over the slider for actual gain and dB; changes ramp over 10 ms.
- See each input channel's peak level during playback, before stereo mixing and volume adjustment. Hover over a meter to see its peak and RMS values in dBFS.
- Start in dark mode and English by default. Use the **Language** menu for Japanese and the **Dark mode** checkbox for a light theme; both choices are saved for the next launch.
- Select tracks or chapters and adjust split points in 0.1-second steps.
- Export multichannel FLAC, a configurable stereo mix, or one mono FLAC per source channel.
- Edit track metadata in a table or paste multiple lines in the bulk editor.
- Read saved Blu-ray track, chapter, and album edits from the earlier `Disc2Flac` app when the disc matches.
- Open a disc drive, a disc folder, or a mounted ISO. Protected sources are unsupported.

## Requirements

- Windows x64.
- .NET 10 SDK to build; .NET 10 Desktop Runtime to run the public build.
- An FFmpeg installation supplied by the user. `ffmpeg.exe` and `ffprobe.exe` are required; `ffplay.exe` is required for preview playback. Place them in a `tools` folder beside the app or make them available on `PATH`.

The live channel meters use FFmpeg's `astats` and `ametadata` filters. Preview playback still works without these filters, but the meters remain unavailable.

The FFmpeg build must provide the demuxers, protocols, and decoders needed for your discs. In particular, FFmpeg's [DVD-Video demuxer](https://ffmpeg.org/ffmpeg-formats.html#dvdvideo) requires GPL library support and `libdvdnav`/`libdvdread`. Consult [FFmpeg's licensing guidance](https://ffmpeg.org/legal.html) for the FFmpeg build you choose. This repository does not contain or download FFmpeg binaries.

## Build

From PowerShell in this directory:

```powershell
./build.ps1
```

The output is in `artifacts/public-win-x64`. It is a framework-dependent, multi-file application. Run `DiscChannelLab.exe` after installing the .NET 10 Desktop Runtime and making the FFmpeg tools available. The public build does not embed FFmpeg or include a single-file executable.

For personal use, `./build-personal.ps1` builds a self-contained single-file executable from **the same source files**. It embeds the FFmpeg executables installed on the builder's computer. Its output is local and is excluded from this repository; it is not the public distribution build. Before distributing such an executable, review the licenses and corresponding-source obligations of every embedded component.

## Development and publication status

`BDDVD2Flac` is the shared application project's historical directory name for both build modes. Feature changes belong there once, so the two builds stay in sync. Existing user settings and saved track edits still use the historical `BDDVD2Flac` data directory. The original application source is offered under [Apache License 2.0](LICENSE); external tools remain under their own licenses. This repository is a development preview of the source code, with no binary release. Source-provenance and dependency review remain open before a tagged release. See [PUBLICATION_CHECKLIST.md](PUBLICATION_CHECKLIST.md).
