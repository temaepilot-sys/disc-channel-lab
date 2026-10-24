# Release checklist

- [x] Use one application source tree for the public and personal build modes.
- [x] Keep FFmpeg binaries, runtime packages, disc images, logs, and build outputs out of Git tracking.
- [x] Provide an FFmpeg-free public build and document the required user-supplied tools.
- [x] Choose Apache License 2.0 for original application code and add the official license text.
- [ ] Add the copyright holder name chosen for public attribution.
- [ ] Complete the source-provenance review before public release.
- [ ] Review third-party notices and the provenance of code based on external format documentation.
- [ ] Review protected-disc handling, including DVD behavior, before making a broad DRM claim.
- [x] Remove personal paths and disc titles from the current publication candidates.
- [x] Replace brand-specific audio labels in the current stream list with format-neutral descriptions.
- [x] Add English and Japanese UI switching with English as the default.
- [ ] Proofread both languages on real discs and verify feature descriptions before release.
- [x] Add playback-time per-channel peak meters with RMS values in tooltips.
- [ ] Design and test a versioned audio-effect extension interface before foobar2000 integration.
- [ ] Review the release artifacts and their license obligations before publishing binary releases.

## Space sketch

- [x] Include viewer sources and the player’s optional PCM link in the repository.
- [x] Build the viewer beside the public player without embedding FFmpeg.
- [x] Document English UI, local data, Input/Post/Output semantics and symbolic particle geometry.
- [x] Include supplied overview and listener screenshots, reviewed for visible personal paths.
- [x] Exclude saved viewer preferences, local endpoint tokens, browser profiles and test audio.
- [x] Integrate all four rendering modes into the shared public/personal source.
- [x] Document matching palettes, vertical flow, camera navigation and mode-preserving Home.
- [x] Include actual WebGL and named-pipe checks with portable test paths.
- [ ] Broaden hardware, GPU and listening-latency checks before a binary release.

## Headphone playback in the main application

- [x] Integrate optional Direct and Steam Audio HRTF playback with the same public/personal sources.
- [x] Retain ordinary playback, standard coefficients and independent in-session fader banks.
- [x] Add English/Japanese controls, one-time trial settings import and metadata backup.
- [x] Pin the external Steam Audio SDK download and retain upstream licensing/dependency notices.
- [x] Document playback/export processing, model approximations, opentrack protocol and viewer timing.
- [ ] Broaden physical tracker, headphone and individual HRTF listening checks before a binary release.

## Spatial Lab integration

- [x] Integrate SOFA comparison, shared binaural export, optional reflections, measured BRIR and supported ADM playback into the common source.
- [x] Keep ordinary stereo as the startup mode and make source presentation selection available in the comparison view.
- [x] Import the approved trial's settings once with metadata backups; retain the old profile and omit caches.
- [x] Retain PureHDF / EBU Common Definitions provenance and license notices.
- [x] Document independent BRIR, fixed head orientation, the ADM subset, buffering and external decoder requirements in English and Japanese.
- [x] Exclude third-party codec executables, recordings, SOFA data and personal presets from the public build recipe.
- [ ] Verify codec patent/license obligations and intended distribution regions before publishing an object-decoding binary package.
- [ ] Broaden real dataset, hardware and perceptual checks; numerical tests are not a certification.
