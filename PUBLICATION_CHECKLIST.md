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

## Space sketch source preview

- [x] Include viewer sources and the player’s optional PCM link in the repository.
- [x] Build the viewer beside the public player without embedding FFmpeg.
- [x] Document English UI, local data, Input/Post/Output semantics and symbolic particle geometry.
- [x] Include supplied overview and listener screenshots, reviewed for visible personal paths.
- [x] Exclude saved viewer preferences, local endpoint tokens, browser profiles and test audio.
- [ ] Broaden hardware, GPU and listening-latency checks before a binary release.
