using System.Windows;
using System.Windows.Controls;

namespace Disc2Flac;

public partial class SpatialLabWindow
{
    CancellationTokenSource? comparisonStop;
    Task? comparisonPlaybackTask;
    int comparisonRequest;
    bool comparisonStale = true, comparisonInputsUpdating, preparingComparison;
    bool? comparisonAtmosEnabled;
    AtmosPresentation comparisonPresentation;
    string auditionLabel = "";
    double auditionDuration, comparisonStartSeconds;

    void ComparisonInput_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready || libraryUpdating || comparisonInputsUpdating) return;
        comparisonInputsUpdating = true;
        try
        {
            if (ReferenceEquals(sender, CompareBrir) && CompareBrir.IsChecked == true)
            {
                CompareRoom.IsChecked = false; FixedYaw.Text = FixedPitch.Text = FixedRoll.Text = "0";
            }
            else if (ReferenceEquals(sender, CompareRoom) && CompareRoom.IsChecked == true) CompareBrir.IsChecked = false;
        }
        finally { comparisonInputsUpdating = false; }
        InvalidateComparison("Settings changed. Press Prepare A/B to render the selected versions.");
    }
    void InvalidateComparison(string message)
    {
        if (!ready || closed) return;
        comparisonStale = true; ComparisonFeedback.Text = message;
        ComparisonFeedback.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); UpdateComparisonControls();
    }
    void ComparisonMessage(string message, bool error = false)
    {
        if (closed) return;
        ComparisonFeedback.Text = message;
        ComparisonFeedback.SetResourceReference(TextBlock.ForegroundProperty, error ? "ErrorBrush" : "TextBrush");
        StatusText.Text = message;
    }
    void UpdateComparisonControls()
    {
        bool playable = comparison is not null && !comparisonStale && operation is null;
        ComparisonPlayAButton.IsEnabled = ComparisonPlayBButton.IsEnabled = playable;
        ComparisonPrepareButton.IsEnabled = operation is null;
        ComparisonStopButton.IsEnabled = comparisonStop is not null;
        ComparisonUse51Button.IsEnabled = operation is null && model.CanChooseAtmosPresentation && model.SelectedAtmosPresentation != AtmosPresentation.Surround51;
        HrtfB.IsEnabled = CompareRoom.IsChecked != true && CompareBrir.IsChecked != true;
        FixedYaw.IsEnabled = FixedPitch.IsEnabled = FixedRoll.IsEnabled = CompareBrir.IsChecked != true;
        LabCancelButton.IsEnabled = operation is not null || comparisonStop is not null;
        LabCancelButton.Content = comparisonStop is not null ? "Stop A/B playback" : "Cancel lab task";
    }
    void UpdateComparisonPresentation()
    {
        bool enabled = model.IsAtmosStreamingEnabled;
        bool changed = comparisonAtmosEnabled is { } previous && (previous != enabled || comparisonPresentation != model.SelectedAtmosPresentation);
        comparisonAtmosEnabled = enabled; comparisonPresentation = model.SelectedAtmosPresentation;
        if (changed) InvalidateComparison("Source presentation changed. Press Prepare A/B to render the selected source again.");
        UpdateComparisonControls();
    }
    async void ComparisonPresentation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || sender is not ComboBox { SelectedItem: AtmosPreviewChoice choice } || choice.Value == model.SelectedAtmosPresentation) return;
        await SelectComparisonPresentationAsync(choice);
    }
    async void ComparisonUse51_Click(object sender, RoutedEventArgs e) =>
        await SelectComparisonPresentationAsync(model.AtmosPreviewChoices.First(p => p.Value == AtmosPresentation.Surround51));
    async Task SelectComparisonPresentationAsync(AtmosPreviewChoice choice)
    {
        if (choice.Value == model.SelectedAtmosPresentation) return;
        if (!model.CanChooseAtmosPresentation)
        {
            ComparisonMessage(model.AtmosPresentationHint, true); return;
        }
        await Job(async ct =>
        {
            ComparisonMessage("Switching the main-player source to " + choice.Label + "…");
            await model.ChangeAtmosPresentationAsync(choice); ct.ThrowIfCancellationRequested();
            if (model.SelectedAtmosPresentation != choice.Value) throw new InvalidOperationException("The source changed before switching completed. Select the source again.");
            UpdateComparisonPresentation();
            ComparisonMessage("Selected " + choice.Label + ". The main player uses the same source. Press Prepare A/B to prepare the comparison.");
        }, pauseMainPlayback: false);
    }
    async void Compare_Click(object sender, RoutedEventArgs e) => await PrepareComparisonAsync();
    async Task PrepareComparisonAsync() => await Job(async ct =>
    {
        comparison = null; comparisonStale = true; preparingComparison = true;
        ComparisonText.Text = "Preparing the selected interval…"; ComparisonPosition.Text = "";
        ComparisonProgress.IsIndeterminate = true; UpdateComparisonControls();
        try
        {
            bool roomComparison = CompareRoom.IsChecked == true, brirComparison = CompareBrir.IsChecked == true;
            comparisonStartSeconds = Value(IntervalStart, 0, 24 * 3600); Value(IntervalLength, 5, 60);
            if (model.SelectedTrack is null || !model.CanPlay && !model.IsPlaying)
                throw new InvalidOperationException("Select a playable track in the main window, then press Prepare A/B.");
            if (comparisonStartSeconds >= model.PreviewMax) throw new InvalidDataException("Start is beyond the selected track. Enter a smaller Start value.");
            if (model.IsAtmosStreamingEnabled && model.SelectedAtmosPresentation == AtmosPresentation.Stereo)
                throw new InvalidDataException("Stereo presentation bypasses HRIR / BRIR. Click 'Use 5.1 for HRIR / BRIR' above (or choose Objects for HRIR only), then Prepare A/B.");
            if (brirComparison && model.IsAtmosStreamingEnabled && model.SelectedAtmosPresentation != AtmosPresentation.Surround51)
                throw new InvalidDataException("BRIR requires fixed speakers. Click 'Use 5.1 for HRIR / BRIR' above, then Prepare A/B. Moving Objects cannot use measured speaker BRIR.");
            var a = model.SpatialSnapshot(A, Pose(), brirComparison ? new() : Room());
            a = a with { Options = a.Options with { SteamTrimDb = 0, SofaTrimDb = 0 } };
            SpatialRenderSnapshot b;
            if (brirComparison)
            {
                if (brirRows.Count == 0) throw new InvalidDataException("Open a BRIR profile on tab 4 (Measured BRIR), then press Prepare A/B again.");
                ComparisonMessage("Validating the BRIR profile…"); var brir = await PrepareEditedBrir(ct);
                b = BrirSnapshot(brir, a); b = b with { Options = b.Options with { BrirTrimDb = 0 } };
            }
            else if (roomComparison)
            {
                b = a with { Options = a.Options with { Room = a.Options.Room with { SoloReflections = false } } };
                a = a with { Options = a.Options with { Room = new() } };
            }
            else
            {
                b = Snapshot(B); b = b with { Options = b.Options with { SteamTrimDb = 0, SofaTrimDb = 0 } };
            }
            // Validate inputs and only the renderer(s) actually used before interrupting main audio.
            model.PausePlayback(); ComparisonMessage("Preparing the selected audio interval…");
            var input = await Prepare(ct); ComparisonMessage("Rendering A and B and measuring their loudness. Audio will play after you press Play A or Play B…");
            var c = await SpatialRendering.CompareAsync(input, a, b, tools.Ffmpeg, MatchLevel.IsChecked == true, ct, brirComparison ? 30 : 6);
            comparison = c; comparisonStale = false;
            string nameB = brirComparison ? BrirName.Text + " (BRIR)" : roomComparison ? A.Name : B.Name;
            ComparisonText.Text = $"A: {A.Name} / {a.Options.Room.Mode} · {c.MetricsA.IntegratedLufs:0.00} LUFS · trim {c.GainADb:0.00} dB\n" +
                $"B: {nameB} / {(brirComparison ? "Measured room" : b.Options.Room.Mode)} · {c.MetricsB.IntegratedLufs:0.00} LUFS · trim {c.GainBDb:0.00} dB\n" +
                $"{input.Frames} identical source frames · {(c.Matched ? "Loudness matched" : "Not matched: disabled / silence / difference above correction limit")}.";
            ComparisonProgress.Maximum = input.Frames / 48000d; ComparisonProgress.Value = 0;
            ComparisonPosition.Text = $"Interval {comparisonStartSeconds:0.000}s → {comparisonStartSeconds + input.Frames / 48000d:0.000}s in the selected track · fixed pose";
            bool same = a.HrtfHash == b.HrtfHash && a.Options.Method == b.Options.Method && a.Options.Room == b.Options.Room;
            bool silent = c.MetricsA.SamplePeak < 1e-8 && c.MetricsB.SamplePeak < 1e-8;
            if (same) ComparisonText.Text += "\nA and B use identical HRIR / room settings.";
            ComparisonMessage(silent ? "Prepared, but both versions are silent. Check the selected interval, main volume and mixer Mute / Solo, then prepare again."
                : same ? "Prepared. A and B use the SAME HRIR and room settings, so no sound difference is expected. Choose another HRIR B or enable HRIR vs BRIR, then prepare again."
                : "Prepared — press Play A or Play B. Click either button during playback to restart that version at the interval start.", silent);
        }
        catch (OperationCanceledException)
        {
            ComparisonMessage("A/B preparation cancelled. Press Prepare A/B to try again."); throw;
        }
        catch (Exception ex)
        {
            ComparisonText.Text = "A/B is not prepared."; ComparisonMessage("A/B not prepared: " + ex.Message, true); throw;
        }
        finally { preparingComparison = false; ComparisonProgress.IsIndeterminate = false; UpdateComparisonControls(); }
    }, pauseMainPlayback: false);

    async void PlayA_Click(object sender, RoutedEventArgs e) => await PlayComparison(false);
    async void PlayB_Click(object sender, RoutedEventArgs e) => await PlayComparison(true);
    async Task PlayComparison(bool b)
    {
        int request = ++comparisonRequest;
        await StopComparisonPlaybackAsync();
        if (closed || request != comparisonRequest) return;
        if (operation is not null) { ComparisonMessage("Wait for A/B preparation to finish, or cancel it first."); return; }
        if (comparison is null || comparisonStale) { ComparisonMessage("Press Prepare A/B before playing the selected versions.", true); return; }
        model.PausePlayback();
        var c = comparison; var audio = b ? c.B : c.A;
        displayInput = audio.Input; SetScene(audio.Input.Scene);
        var stop = new CancellationTokenSource(); var control = new PreviewPlaybackControl();
        comparisonStop = stop; transport = control; auditionLabel = b ? "B" : "A"; auditionDuration = audio.SourceFrames / 48000d;
        ComparisonProgress.Maximum = auditionDuration; ComparisonProgress.Value = 0;
        ComparisonMessage("Starting " + auditionLabel + " from the prepared interval start…"); UpdateComparisonControls();
        var mainInteraction = LockMainInteraction();
        comparisonPlaybackTask = Run(); await comparisonPlaybackTask;
        async Task Run()
        {
            bool failed = false;
            try
            {
                await SpatialPcmPlayer.PlayCachedAsync(audio, b ? c.GainBDb : c.GainADb, control, stop.Token);
                if (!closed) { ComparisonProgress.Value = auditionDuration; ComparisonPosition.Text = auditionLabel + " finished · " + TimeSpan.FromSeconds(auditionDuration).ToString(@"mm\:ss\.f"); }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex) { failed = true; comparisonStale = true; ComparisonMessage("A/B playback failed: " + ex.Message + " Press Prepare A/B again.", true); }
            finally
            {
                using var releaseMainInteraction = mainInteraction;
                if (stop.IsCancellationRequested && !closed)
                    ComparisonPosition.Text = "Stopped " + auditionLabel + " · " + TimeSpan.FromSeconds(Math.Clamp(control.EstimatedPosition, 0, auditionDuration)).ToString(@"mm\:ss\.f") +
                        " / " + TimeSpan.FromSeconds(auditionDuration).ToString(@"mm\:ss\.f");
                control.Dispose(); stop.Dispose();
                if (ReferenceEquals(comparisonStop, stop))
                {
                    comparisonStop = null; transport = null;
                    if (!closed)
                    {
                        if (!failed) ComparisonMessage(comparisonStale ? "Settings changed. Press Prepare A/B again."
                            : "A/B playback stopped / finished. Press Play A or Play B to listen again. Main track position is preserved.");
                        UpdateComparisonControls();
                    }
                }
            }
        }
    }
    async Task StopComparisonPlaybackAsync()
    {
        var task = comparisonPlaybackTask; comparisonStop?.Cancel();
        if (task is not null) await task;
    }
    void StopComparison_Click(object sender, RoutedEventArgs e) { comparisonRequest++; comparisonStop?.Cancel(); }
    void UpdateComparisonPosition()
    {
        if (comparisonStop is null || transport is not { } playback || preparingComparison) return;
        double seconds = Math.Clamp(playback.EstimatedPosition, 0, auditionDuration);
        ComparisonProgress.Value = seconds;
        ComparisonPosition.Text = (playback.HasStarted ? "Playing " : "Starting ") + auditionLabel + " · " +
            TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss\.f") + " / " + TimeSpan.FromSeconds(auditionDuration).ToString(@"mm\:ss\.f") +
            $" · track {comparisonStartSeconds + seconds:0.000}s";
        if (playback.HasStarted && ComparisonFeedback.Text.StartsWith("Starting ", StringComparison.Ordinal))
            ComparisonMessage("Playing " + auditionLabel + ". Click A / B to switch from the interval start, or Stop A/B to finish.");
    }
}
