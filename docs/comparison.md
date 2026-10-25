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

## Hear the added reflections

On **3 · HRIR / Reflections**, choose **Listening Room**, enable **Reflections only**, then use Play in the main player. This isolates the added paths by muting direct sound and LFE. Disable Reflections only to restore the combined sound. **Prepare OFF / ON A/B** compares the same source and HRIR, optionally at matched loudness.

Studio and Listening Room use only six first-order paths per non-LFE source. The configured −18 / −12 dB is per wall **before** distance loss and absorption. In an original independent-channel noise test, reflected/dry RMS measured about −24.7 / −21.6 dB, with excess delays of 3–13 / 4–17 ms. These figures depend on the source, HRTF and room; they are not universal levels or a listening assessment. Such short, quiet paths mainly change spatial cues and timbre rather than create a separate echo or hall reverb.

For a more obvious audition, try **Listening Room → −6 dB**, then compare or isolate the reflections. Existing presets and saved settings are unchanged. Stronger settings may increase peaks; watch the clipped-sample count and reduce gain if needed. Recordings that already contain room sound may change only subtly or become less natural.

**Big Room** is an additional test preset: 10 × 14 × 6 m, 0 dB per wall before distance and absorption, and low/mid/high absorption of 0.08/0.15/0.30. In the same synthesized 5.1 test, reflected/dry RMS was about −11.9 dB and the longest excess path about 41 ms, roughly 10 dB more reflected energy than Listening Room. Actual results depend on the source and HRIR. Use **Reflections only** to isolate the added sound. This still uses six first-order paths, without a church-like late reverb. Existing presets and the default OFF state are retained.

Atmos **Stereo presentation** deliberately bypasses HRIR and added reflections. Applying an active room mode there is rejected before changing playback; choose **Objects** or **5.1** on tab 2 first. **Measured BRIR** is a separate room response and bypasses this added reflection model.

## Export

Use the lower part of this page for fixed-pose 48 kHz float WAV export, selecting the interval or whole track. Enable **Use loaded Measured BRIR** to export that independent renderer. The main player's **Save FLAC** handles selected-track binaural FLAC when headphone rendering is enabled. A `.render.json` sidecar records the settings. Exports do not record head-tracker motion; per-track exports start with independent DSP history.

## If preparation fails

Read the red message immediately below Prepare / Play. Select a playable track, enter a Start before its end, use compatible HRIR data, and load/confirm explicit BRIR routing. For an Objects or Stereo presentation error, press **Use 5.1 for HRIR / BRIR** on the same page. Disabled selectors explain the source prerequisite. No success state is shown for failed or cancelled preparation.
