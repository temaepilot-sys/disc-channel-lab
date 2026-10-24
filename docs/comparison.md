# Spatial Lab — Compare / Export

Open **HRTF / room… → 2 · Compare / Export** from the main player. The source is the selected main-player track. Start is measured from that track's beginning; comparison length is 5–60 seconds, limited to the remaining track duration.

## Compare HRIR and measured BRIR

1. Open a playable source and select a track. For TrueHD Atmos, press **Enable ADM playback** in the main source panel.
2. Load your explicit speaker/ear response profile on **4 · Measured BRIR → Open BRIR profile…**.
3. Return to **2 · Compare / Export** and enable **HRIR vs BRIR**. Choose HRIR A, initially Steam Audio built-in. Unused HRIR B is disabled; head orientation is fixed at 0° for BRIR.
4. For progressive Atmos, press **Use 5.1 for HRIR / BRIR** at the top of this comparison page. It selects the actual six-channel presentation, retaining the main track and position.
5. Set Start and Length, then press **Prepare A/B**. Preparation pauses the main player, renders both versions and measures loudness. It does not start playback.
6. After **Prepared** appears below the buttons, press **Play A from start** for HRIR or **Play B from start** for BRIR. Either button restarts its version at the comparison interval's beginning. Switching stops the previous audition before opening the next.
7. **Stop A/B** returns to the main controls, retaining the main track position and prepared comparison.

The source presentation selector is always visible in both windows. It is enabled only for progressive TrueHD Atmos. Ordinary audio retains its original channel layout. A standalone ADM scene does not necessarily contain a separate 5.1 or stereo presentation; use the original TrueHD source or channel-based audio for measured speaker BRIR. Moving Objects cannot be processed as fixed measured speakers.

## Other comparisons

- Turn both comparison options off to compare HRIR A with HRIR B. Choosing the same built-in HRIR twice produces the same sound; the readiness message explicitly reports this.
- **HRIR vs room** compares HRIR A without reflections against the same HRIR with the settings on **3 · HRIR / Reflections**. This option and HRIR vs BRIR are mutually exclusive.
- Loudness matching uses constant attenuation only, with a correction limit of 6 dB for HRIR comparisons and 30 dB for HRIR/BRIR. Silence and out-of-range differences are reported as unmatched. Equal measured loudness does not guarantee equal perceived loudness.

Changes to source, source presentation, interval or comparison controls invalidate prepared audio. Press Prepare again. Master volume and mixer settings are captured at preparation; prepare again to compare subsequent changes.

## Export

Use the lower part of this page for fixed-pose 48 kHz float WAV export, selecting the interval or whole track. Enable **Use loaded Measured BRIR** to export that independent renderer. The main player's **Save FLAC** handles selected-track binaural FLAC when headphone rendering is enabled. A `.render.json` sidecar records the settings. Exports do not record head-tracker motion; per-track exports start with independent DSP history.

## If preparation fails

Read the red message immediately below Prepare / Play. Select a playable track, enter a Start before its end, use compatible HRIR data, and load/confirm explicit BRIR routing. For an Objects or Stereo presentation error, press **Use 5.1 for HRIR / BRIR** on the same page. Disabled selectors explain the source prerequisite. No success state is shown for failed or cancelled preparation.
