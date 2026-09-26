namespace Disc2Flac;

public sealed partial class MainViewModel
{
    public void StopInspection()
    {
        _inspection?.Cancel();
        _inspection = null;
        InspectionProgress = "";
    }

    private void StartBackgroundInspection(bool force = false)
    {
        StopInspection();
        if (_disc is null) return;
        var cancellation = new CancellationTokenSource();
        _inspection = cancellation;
        InspectionTask = InspectRemainingAsync(_disc, cancellation, force);
    }

    public void InspectAllTitles() { if (CanInspect) StartBackgroundInspection(force: true); }

    private async Task InspectRemainingAsync(DiscAnalysis disc, CancellationTokenSource cancellation, bool force)
    {
        try
        {
            var titles = disc.Playlists.Where(x => force || x.Availability == AudioAvailability.Pending).ToArray();
            for (var i = 0; i < titles.Length; i++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                InspectionProgress = $"{LanguageService.T("音声を確認中")} {i + 1}/{titles.Length}";
                // Never replace the user's current rows or stream selection from a background scan.
                await _discService.InspectPlaylistAsync(disc, titles[i], cancellation.Token,
                    force && titles[i] != _loadedPlaylist);
            }
            if (_inspection == cancellation) InspectionProgress = LanguageService.T("ディスクの診断が完了しました。");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Write($"BACKGROUND INSPECTION: {ex}"); }
        finally
        {
            if (_inspection == cancellation) _inspection = null;
            cancellation.Dispose();
        }
    }

    public async Task RetrySelectedTitleAsync()
    {
        if (!CanInspect || _disc is null || SelectedPlaylist is null) return;
        var title = SelectedPlaylist;
        SaveEdits();
        BeginWork("音声を確認中");
        try
        {
            await _discService.InspectPlaylistAsync(_disc, title, _work!.Token, force: true);
            await LoadPlaylistCoreAsync(title, _work.Token);
            Status = PlaylistStatus(false);
        }
        catch (Exception ex) { HandleError(ex); }
        finally { EndWork(); }
    }

    public void ToggleShortTail(bool merge)
    {
        if (!CanToggleTail || _disc is null || _loadedPlaylist is null || merge == MergeShortTail) return;
        StopInspection();
        var playlist = _loadedPlaylist;
        if (!SaveEdits()) return;
        var prior = playlist.MergeShortTail;
        var rows = Tracks.ToArray();
        try
        {
            playlist.MergeShortTail = merge;
            var saved = _editStore.Load(_disc, playlist);
            var adapted = saved ?? ChapterCorrection.Apply(rows, playlist);
            // Preserve newly edited metadata on rows shared by both boundary modes.
            foreach (var row in adapted)
                if (rows.FirstOrDefault(x => x.StartTicks == row.StartTicks) is { } previous && !ReferenceEquals(row, previous))
                { row.Title = previous.Title; row.Artist = previous.Artist; row.IsSelected = previous.IsSelected; }
            _editStore.Save(_disc, playlist, adapted);
            _editStore.SaveMergeShortTail(_disc, playlist);
            ReplaceTracks(adapted);
            Status = ChapterCorrectionNote;
        }
        catch (Exception ex) { playlist.MergeShortTail = prior; HandleError(ex); }
        UpdateActions();
    }

    public async Task SaveDiagnosticsAsync(string path)
    {
        if (_disc is null) return;
        try
        {
            await DiscDiagnostics.SaveAsync(_disc, path, CancellationToken.None);
            Status = LanguageService.T("診断レポートを保存しました。");
        }
        catch (Exception ex) { HandleError(ex); }
    }
}

public static class ChapterCorrection
{
    public static IReadOnlyList<TrackRow> Apply(IReadOnlyList<TrackRow> tracks, PlaylistInfo playlist)
    {
        var rows = tracks.Select(x => new TrackRow { Number = x.Number, StartTicks = x.StartTicks, EndTicks = x.EndTicks,
            Title = x.Title, Artist = x.Artist, IsChapter = x.IsChapter, IsSelected = x.IsSelected }).ToList();
        if (!playlist.HasShortTail || rows.Count == 0) return rows;
        var boundary = playlist.ChapterStarts[^1];
        if (playlist.MergeShortTail && rows.Count > 1 && rows[^1].StartTicks == boundary &&
            rows[^2].EndTicks == boundary && rows[^1].EndTicks == playlist.DurationTicks)
        { rows[^2].EndTicks = rows[^1].EndTicks; rows.RemoveAt(rows.Count - 1); }
        else if (!playlist.MergeShortTail && rows[^1].StartTicks < boundary && rows[^1].EndTicks == playlist.DurationTicks)
        {
            var previous = rows[^1];
            previous.EndTicks = boundary;
            rows.Add(new TrackRow { Number = rows.Count + 1, StartTicks = boundary, EndTicks = playlist.DurationTicks,
                IsChapter = true, IsSelected = previous.IsSelected, Artist = previous.Artist,
                Title = playlist.ChapterTitle(playlist.ChapterStarts.Count) ?? $"Chapter {playlist.ChapterStarts.Count:00}" });
        }
        return rows;
    }
}
