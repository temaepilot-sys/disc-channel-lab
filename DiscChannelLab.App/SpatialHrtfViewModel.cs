namespace Disc2Flac;

public sealed partial class MainViewModel
{
    public async Task ApplySpatialBrirAsync(PreparedBrir brir, CancellationToken token = default)
    {
        if (IsBusy) throw new InvalidOperationException("Wait for the current operation before changing renderer.");
        if (CurrentAdm is not null && (!IsAtmosStreamingEnabled || SelectedAtmosPresentation != AtmosPresentation.Surround51))
            throw new NotSupportedException("For Atmos BRIR playback, use tab 2 (Compare / Export) → 'Use 5.1 for HRIR / BRIR'. Moving ADM objects are not fixed BRIR speakers; a standalone ADM file has no separate 5.1 presentation.");
        string[]? channels = IsAtmosStreamingEnabled ? CurrentAdmScene?.Sources.Select(s => s.Id).ToArray()
            : SelectedStream is { } stream ? HeadphoneGeometry.Channels(stream) : null;
        if (channels is not null) brir.RequireChannels(channels);
        token.ThrowIfCancellationRequested();
        HeadphoneSession.Current.Recenter();
        await ApplySpatialOptionsAsync(HeadphoneSession.Current.Options with { Enabled = true, Method = HeadphoneMethod.MeasuredBrir,
            BrirPath = brir.Path, BrirHash = brir.Hash, UseTracker = false, Room = new() });
    }
    /// <summary>Validate off the audio thread, then rebuild fixed native HRTF state at the held playhead.</summary>
    public async Task ApplySpatialHrtfAsync(HrtfEntry entry, CancellationToken token = default)
    {
        if (CurrentAdm is not null && !IsAdmPlaybackEnabled) throw new InvalidOperationException("Enable ADM playback in the main window first.");
        if (IsBusy) throw new InvalidOperationException("Wait for the current operation before changing HRTF.");
        // Validate the registered hash before interrupting playback. Retain other live settings at commit time.
        var selected = await Task.Run(() => SofaLibrary.Apply(entry, new HeadphoneOptions()), token);
        token.ThrowIfCancellationRequested();
        if (IsBusy) throw new InvalidOperationException("Wait for the current operation before changing HRTF.");
        var session = HeadphoneSession.Current;
        var options = session.Options with { Enabled = true, Method = HeadphoneMethod.SteamAudio,
            Hrtf = selected.Hrtf, SofaPath = selected.SofaPath };
        await ApplySpatialOptionsAsync(options);
    }
    public async Task ApplySpatialRoomAsync(SpatialRoomOptions room, CancellationToken token = default)
    {
        if (CurrentAdm is not null && !IsAdmPlaybackEnabled) throw new InvalidOperationException("Enable ADM playback in the main window first.");
        if (IsBusy) throw new InvalidOperationException("Wait for the current operation before changing reflections.");
        room.Validate();
        if (room.Enabled && IsAtmosStreamingEnabled && SelectedAtmosPresentation == AtmosPresentation.Stereo)
            throw new InvalidOperationException("Stereo presentation bypasses room reflections. Choose Objects or 5.1 in Source presentation on tab 2 (Compare / Export), then select the room mode again.");
        var session = HeadphoneSession.Current;
        var scene = CurrentAdmScene ?? (SelectedStream is { } stream
            ? SpatialScene.ChannelBed(HeadphoneGeometry.Channels(stream), 256, session.Options.FrontAngle) : null);
        if (room.Enabled && scene is not null)
            await Task.Run(() =>
            {
                foreach (var source in scene.Sources.Where(s => !s.IsLfe))
                    foreach (var point in source.Blocks) { token.ThrowIfCancellationRequested(); EarlyReflections.Path(SpatialSource.ToWorld(point) * (float)room.SpeakerRadius, room, 0); }
            }, token);
        token.ThrowIfCancellationRequested();
        if (IsBusy) throw new InvalidOperationException("Wait for the current operation before changing reflections.");
        await ApplySpatialOptionsAsync(session.Options with { Room = room,
            Enabled = room.Enabled || session.Options.Enabled,
            Method = room.Enabled || session.Options.Method == HeadphoneMethod.MeasuredBrir ? HeadphoneMethod.SteamAudio : session.Options.Method });
    }
    async Task ApplySpatialOptionsAsync(HeadphoneOptions options)
    {
        var session = HeadphoneSession.Current;
        bool restart = IsPlaying && !SamePlaybackEngine(_playbackEngineOptions, options);
        if (restart) PausePlayback();
        session.SetOptions(options);
        session.Save();
        // HRTF handles belong to the renderer constructor. Updating Options alone cannot replace them.
        // A paused session stays paused; Play will rebuild it if its engine identity changed.
        if (restart) await TogglePlaybackAsync();
    }
}
