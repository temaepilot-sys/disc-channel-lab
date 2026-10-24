# Measured BRIR

Open **Spatial Lab → 4 · Measured BRIR**. This is an independent headphone renderer. It replaces HRIR rather than following it; Direct filters, Steam Audio HRTF and added early reflections are bypassed. Ordinary stereo remains the startup default.

## Setup

1. Open audio in the main window. Add measured stereo WAV or SOFA response files on the BRIR tab, or open a BRIR JSON profile.
2. Explicitly map every input speaker (`FL FR FC SL SR BL BR BC LFE`). For SOFA, select the zero-based M measurement index. Duplicate a route to use another measurement from the same file.
3. Set the left/right **ear** indices (normally 0/1). These are not physical speaker L/R. Check the source documentation for speaker positions and neutral head orientation. M may represent head rotations, not distinct speaker positions.
4. Confirm the mapping. If no LFE measurement exists, you may explicitly allow centered dry LFE. Otherwise missing routes are rejected; no channel is guessed or omitted.
5. Choose **Validate and apply BRIR**. Validation leaves current audio playing; a successful renderer change briefly switches DSP and resumes at the held position. Failed validation leaves the original playback untouched. A paused player stays paused; use the main Play button when stopped. Mixer gain, Mute, Solo, polarity and Master remain active. Save the profile separately from disc metadata. Original measurement files are left untouched.
6. **Return to Pure HRIR**, or choose Stereo / Direct / HRTF in Preview Audio to leave BRIR mode.

No measured dataset is bundled. A profile references external measurement files; moving or changing them requires revalidation. Profile and asset SHA-256 values identify the actual data.

## Compare and export

**Prepare HRIR / BRIR A/B** uses HRIR A and the loaded BRIR at the same neutral head orientation, with no added reflections. Choose a 5–60 second interval. Constant attenuation matches measured integrated LUFS, allowing up to 30 dB difference, with common peak headroom. Silence/out-of-range results are visibly not matched. This does not equalize timbre, personalize localization or reconstruct preceding reverb history.

For FLAC, apply BRIR, select **Render headphone / reflection settings to stereo FLAC** in the main window and use Save FLAC. For float32 WAV, select **Use loaded Measured BRIR for float WAV export** on Compare / Export. Realtime and offline use identical shared DSP, including filter tails. Export sidecars record routing, hashes, conversion delay, gains and engine version. FLAC rejects above-unity peaks; automatic normalization is off. The engine runs at 48 kHz. Existing binaural output options are 16-bit/44.1 kHz or 24-bit/48 kHz.

Per-song export starts fresh convolution history and retains the room tail; it does not render a whole album with continuous DSP history. Device guard fades and Windows device volume are outside the shared render bus.

## Supported data

- Stereo RIFF WAV: PCM16/24/32 or float32, 8–192 kHz.
- SOFA FIR `M×2×N`, one sampling rate in hertz, explicit M and ear mapping. `Data.Delay` must be `1×2` or `M×2`; nonnegative integer sample delays are preserved.
- Maximum original response including delay: six seconds. Maximum source file: 512 MiB. No silent tail truncation.
- Non-48 kHz IRs use windowed-sinc conversion with transfer-gain scaling. Both filter wings are retained by adding a documented common 192-frame / 4 ms conversion delay to **all routes in that profile**. Original interaural/measurement delay differences remain. All-48 kHz profiles need no conversion delay; the partitioned convolver itself adds no block latency.
- Unsupported: fractional `Data.Delay`, FIR-E/multiple emitters, TF, dynamic head rotation, moving ADM objects, Hybrid, direct/early/late decomposition. These are rejected or disabled rather than approximated.

For progressive Atmos select the **5.1 presentation** before applying BRIR. Switching to Objects returns to Pure HRTF. Static measured speaker responses are not an object renderer. The Headphones diagram is a nominal channel sketch, not a reconstruction of the measured room; angle and tracking controls are disabled for BRIR.

## Data sources and verification

Candidate datasets: [IEM Production Studio](https://opendata.iem.at/projects/binauralroomresponses/) ([measurement readme](https://phaidra.kug.ac.at/api/object/o:76742/comp/COMP000002)) and [WDR broadcast studios](https://zenodo.org/records/3930833). Review their documentation, positions, head orientations and usage conditions. Their actual files and listening quality have **not** been verified in this environment; blanket compatibility is not claimed.

References: [SOFA data and delay definitions](https://www.sofaconventions.org/mediawiki/index.php/SOFA_specifications), [Steam Audio custom HRTF restrictions](https://valvesoftware.github.io/steam-audio/doc/capi/guide.html#custom-hrtfs).

Synthetic fixtures verify independent direct-convolution agreement, FFT partition boundaries and tails, ear mapping/delays, resampling, shared realtime/offline float PCM, FLAC sidecars, real WASAPI scheduling with silent output, and real WPF controls. Synthetic IRs are numerical fixtures, not substitute room measurements. Personalized externalization, musical preference, actual measured datasets and long-duration high-load operation remain unverified. Keep Pure HRIR as the listening reference, especially for material that already contains substantial reverberation.
