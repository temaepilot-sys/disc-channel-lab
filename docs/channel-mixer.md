# Channel mixer

[日本語](channel-mixer.ja.md)

Select a supported multichannel stream and choose **Open mixer**. Opening it for the first time with a supported stream enables **Apply mixer to playback**. The window is independent of the main controls; closing it preserves playback and settings. Mono and stereo sources continue to use the main player controls. The mixer supports 28 layouts with 3–8 input channels.

## Levels and meters

- Each channel has Input, Post L/R, a vertical fader, numeric gain, Mute, and pan. The main window retains its input meters and playback volume control.
- Channel gain ranges from 0–100%. The fader has a listening curve; numeric input is the actual amplitude percentage. At 100%, there is no extra group normalization. Default Front gain is 100%, Center/Surround about 70.71%, and LFE 0%.
- Pan uses an equal-power curve. Left channels initially go left, right channels go right, and center channels and LFE go to the center. FL at full left and FR at full right have matching Input/Post peaks at 100%. A centered signal is split between both sides, about −3 dB on each side.
- Master shares the player's 0–200% volume setting. **Reset defaults** resets channel levels, pan and mute while keeping Master unchanged.
- Input is before mixing. Post L/R is each channel's contribution after fader/pan and before Master. Output L/R is the sum after Master, before clipping. All use the same 20 ms PCM windows and playback clock. Red indicates 0 dBFS or above.
- Unchecking **Apply mixer to playback** uses the standard stereo mix. Selecting a single channel in the main player allows solo listening; adjusting the enabled mixer returns to stereo listening.

## FLAC export

Choose **Export this mix to FLAC** in the mixer, or check **Export stereo FLAC using channel mixer settings** in the main window. Export uses the selected tracks, quality and destination from the main window and stops playback when starting.

Fader, pan and mute use the same coefficients as preview playback. Master is excluded by default; check **Include Master volume in export** to include it. Settings are captured when the job starts, so later changes do not alter an export already in progress.

The mix name appears in the filename as `[Mixer - name]`. `MIXER_VARIANT` and `MIXER_SETTINGS` tags record the variant and settings. Existing files receive unique names instead of being overwritten. Completed files are checked for channel count, sample rate, bit depth and sample count before moving into place.

There is no automatic normalization or limiter. Mixing uses floating point, then values outside the integer FLAC range saturate rather than wrap around. Excessive summed levels can still cause audible clipping.

## JSON presets

Use **Save preset…** and **Load preset…** to keep mixer settings in a separate JSON file. Presets contain the mix name, per-channel gain/pan/mute, Master percentage and curve, and whether Master is included in export. They contain no disc identity, track metadata or output paths.

Presets use format `DiscChannelLab.MixerPreset`, version `1`. Channel gain is 0–1 and pan is −1 (left) to +1 (right). Master is 0–200 percent. Loading matches channel codes, not their position in the JSON array, so the same preset works on another disc with the same channel configuration. A different configuration, malformed data, missing fields, out-of-range values or unsupported version is rejected without changing current settings. Successful loading enables the mixer for playback.

During the session, shared channel settings are retained when changing streams or discs. Save a preset to reuse them after restarting the app. Disc metadata remains independent.

## Developer checks

Build `DiscChannelLab.Verify` in Release mode, then run these commands with the required FFmpeg tools on `PATH`. Use an empty output directory for export checks.

```powershell
dotnet build DiscChannelLab.Verify -c Release
dotnet DiscChannelLab.Verify/bin/Release/net10.0-windows/DiscChannelLab.Verify.dll mixer-prototype-test artifacts/mixer-checks
dotnet DiscChannelLab.Verify/bin/Release/net10.0-windows/DiscChannelLab.Verify.dll meter-alignment-test
dotnet DiscChannelLab.Verify/bin/Release/net10.0-windows/DiscChannelLab.Verify.dll mixer-export-test artifacts/mixer-export-checks
```
