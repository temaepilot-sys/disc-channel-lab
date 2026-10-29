# Object audio / ADM guide

## What this feature does

DiscChannelLab reads source positions and renders two headphone channels with the selected HRTF. Two output channels are expected: this is binaural rendering, rather than simply discarding object positions.

Use unprotected material you created or are authorized to use. No copy protection removal is provided. This experimental point-source renderer is not a certified Dolby Atmos renderer and does not promise to reproduce the original production exactly.

## Supported input

- Position-aware ADM BWF / RF64 / BW64: 48 kHz PCM or float, within the supported Objects / DirectSpeakers subset.
- TrueHD Atmos: PCM and position metadata decoded by external truehdd 0.6.2. FFmpeg is also required. The public package does not bundle either tool.
- Ordinary stereo, 5.1 and 7.1: existing speaker-based playback. Channel PCM alone cannot recover Atmos object positions.
- E-AC-3 JOC object decoding, all advanced ADM features and protected input are not supported.

## Start playback

1. Open a drive, folder or audio file.
2. Detection enables Enable ADM playback in the left source panel. Detection alone never changes playback mode.
3. Press that button. TrueHD Atmos prepares a short read-ahead, rather than waiting for a complete ADM export. Select truehdd.exe if it is not configured.
4. Select a track and use the main Play, Pause, Previous / Next and timeline controls.
5. Open ADM scene to inspect positions. Choose a SOFA under HRTF / room. Start comparisons with Pure HRTF, without added reflections.

## Seeking and buffering

Backward seeks within prepared audio use the cached PCM and matching metadata. A seek beyond the prepared range advances the stateful decoder to that position. Prepared in the source panel shows progress.

When the selected position needs preparation, Prepared is bold red and a notice explains the delay. After Play, the notice says Preparing playback — please wait and identifies the requested position. It returns to the normal style when audio starts. A position selected while paused is prepared when you press Play.

A large first forward seek may take time. Object state and decoder history are not guessed for an instant restart. Read-ahead runs separately from the audio/UI threads; the in-memory audio queue is bounded.

Pause stops after a small amount of read-ahead. Disk cache grows to the prepared position, so seeking far into a long title can use substantial disk space. Disabling ADM playback or closing the application releases this session's temporary audio. Cache control can remove files left after an abnormal shutdown. Track names, album names and SOFA preferences are stored separately.

## Compare stereo, 5.1 and objects

During progressive TrueHD Atmos playback, use Compare source presentation.

- Objects renders the decoded positional sources through the selected headphone renderer (initially HRTF).
- 5.1 presentation renders the same bitstream's six-channel presentation at the standard virtual speaker positions through that headphone renderer.
- Stereo presentation plays the same bitstream's two-channel presentation directly to left and right, bypassing HRTF and added room reflections.

Changing presentation retains the track and position, with a brief renderer restart. These are presentations of the same bitstream, not automatically located separate stereo/5.1 album releases. Mixes, coefficients and rendering can produce different levels and timbres. This selector does not automatically match loudness. Compare the same interval at matched listening levels.

## Inspect positions and use Solo

The amber floor arrow and the FRONT guide mark the source space's front. Rotating the diagram changes only the inspection camera, not the audio listener's orientation.

The default view looks forward from behind the listener, so left and right sources match the listener's sides. Looking back from in front of the listener naturally reverses their screen positions. Home restores the default rear view; it does not change the headphone pose.

Left drag / arrow keys: orbit and overhead view. Right drag: pan. Wheel: zoom. Home: restore the default view.

Each sphere has a readable label that follows its position and matches the table's Label column. Decoded `Object 12` names retain their original number; speaker beds show their channel name. Other ADM names use a `#` reference from the source list. Labels hide with inactive or off-screen sources.

Use **Show labels** beside **Clear Solo** to show or hide the labels and their leader lines. The choice is remembered when Spatial Lab closes. Labels are enabled by default.

Click a sphere to Solo that source, and click again to release it. Shift-click or the table checkboxes select multiple Solo sources. Clear Solo / Esc releases all.

A decoded spatial source is not necessarily an isolated instrument, voice or original studio stem. Multiple sounds may be coded together. Displayed positions are not a physical room simulation or a guarantee of perceived localization.

Solo is preview only. It does not affect Save ADM or FLAC exports. It clears when the Lab closes, the presentation changes, ADM is disabled or another source opens.

## Save audio

Save ADM creates one full-title BW64 containing audio, positions and chapter boundaries. This explicit archival operation needs complete preparation and substantial free disk space.

Before saving, edit the disc name, title/album name, track names and artists in the main window. Save ADM embeds the current list and edited boundaries inside the WAV, including every track in the title. Reusing an older prepared ADM still applies the latest edits without decoding again. Reopen the saved WAV with Open audio files to restore this information; no separate track-information file is needed. Disc and title names remain separate, and any available date, genre, publisher and notes are retained. Audio samples and spatial positions are unchanged, and the prepared source file is preserved. Track details use DiscChannelLab's embedded `dclc` metadata; other ADM applications may show only the standard programme name. Existing exports are preserved: save a new file to update an older export.

After a successful ADM WAV or FLAC export, a completion dialog shows the destination. Cancellation or a failed save does not show a success notification. Explicit settings, profile, evaluation and diagnostic saves also show completion; automatic preference and track-edit saves stay silent.

For a BD title with one or more clips, export starts a strict external decoder for each PlayItem, avoiding decoder state carried across incompatible boundaries. It joins explicitly mapped PCM tracks and source metadata at sample boundaries, without interpolating into the next clip. When audio ends before the video interval, source PTS and duration must establish the empty timestamp region before silence is inserted. At most one access unit extending past the requested endpoint can be trimmed; unexplained missing audio or duration differences remain errors. The `.import.json` records these adjustments. Source media and stored track edits are untouched. Metadata assembly and WAV writing run in the background.

Some valid MPEG-TS audio streams do not report an overall duration. In that case, export scans the selected audio stream's packet timestamps to the end of the clip with bounded memory. It accepts the first and last timestamps only after checking that all audio packets form a continuous timeline; missing timestamps, internal gaps, overlaps or corrupt packets remain errors. It does not substitute the video's duration for the audio's duration.

Save FLAC writes selected tracks as two rendered channels. Default quality is 16-bit / 44.1 kHz; rendered output also offers 24-bit / 48 kHz. Names distinguish [Speaker Stereo], [Headphone Direct] and [Headphone Binaural]. Speaker stereo is the same two-channel file for ordinary speakers and an amplifier with an optional subwoofer. FLAC does not retain the object position metadata.

Export fixes the head pose at the start of the operation; it does not record tracker movements. **Also save ADM / headphone render settings as JSON** is OFF by default. Enable it in the main FLAC panel to write a `.render.json` beside each FLAC, recording SOFA hashes, pose, mixer, gain, LFE, reflections and other reproduction settings. It is an optional record for reproducing the render; FLAC playback and its embedded song/artist tags do not require it. With the option OFF, export also skips the extra loudness-measurement pass used only for that record. Existing JSON files are preserved. Individual-track exports initialize DSP independently; continuous-album convolution history across separate files is not yet supported.

ADM preparation, playback and track export restore the original sample-based chapter anchors and PCM endpoint after converting the displayed time. This avoids a one-sample overrun on the last track and keeps adjacent tracks contiguous. An interval genuinely outside the source remains an error.

To preserve the ordinary source channels as multichannel FLAC, disable ADM and use the original-source export options. No automatic normalization is applied.

## Troubleshooting

- Source reading interrupted: check that the disc/ISO is still mounted and the drive remains connected, then retry. FFmpeg read errors stop ADM preparation and live object decoding immediately; a partial decode is not accepted as a completed archive. Demuxer errors and selected input arguments are recorded in the local log. DiscChannelLab does not automatically unmount disc images.
- Disabled ADM button: check that the selected title contains supported ADM or TrueHD Atmos.
- Unexpected level: check HRTF trims, common headroom, LFE, master and OS processing. Default headphone headroom remains -12 dB. The direct stereo presentation bypasses headphone headroom as well as HRTF.
- Slow seek: the first seek beyond Prepared must buffer. Cancel or change the source to stop preparation.
- Dropouts: try Pure HRTF without extra reflections and reduce other heavy processing. Sources are not silently dropped.
- Unsupported source or tool error: return to ordinary channel playback. Protection removal and guessed metadata are not used.

## Rights and distribution

The application license does not grant rights in recordings, third-party patents or trademarks. Private listening, saving copies and publishing extracted audio are different uses and require separate rights analysis. A Solo control does not make every use lawful.

Dolby and Dolby Atmos are trademarks of Dolby Laboratories. No affiliation, endorsement or certification is claimed.
