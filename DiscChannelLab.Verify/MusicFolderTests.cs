using Disc2Flac;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

internal static class MusicFolderTests
{
    public static void Run(string output)
    {
        var root = Path.GetFullPath(output); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", Path.Combine(root, "profile"));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App(); app.InitializeComponent(); WpfTestApplication.SuppressStartupWindow(app);
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                Pump(Test());
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();

        async Task Test()
        {
            var checks = new List<string>();
            void Check(bool ok, string description)
            {
                if (!ok) throw new InvalidDataException(description);
                checks.Add(description); Console.WriteLine("PASS " + description);
            }
            var log = new AppLog(Path.Combine(root, "logs")); var tools = new ToolPaths();
            var runner = new ProcessRunner(log); var probe = new FfprobeService(tools, runner);
            var service = new DiscService(probe, log);
            var tagged = Path.Combine(root, "Tagged album"); var natural = Path.Combine(root, "Natural album");
            Directory.CreateDirectory(tagged); Directory.CreateDirectory(natural);
            async Task<string> Generate(string folder, string name, string codec, int rate, int channels,
                string? track = null, string? disc = null, bool aliases = false)
            {
                var file = Path.Combine(folder, name);
                var layout = channels == 6 ? "5.1" : channels == 1 ? "mono" : "stereo";
                var expression = string.Join('|', Enumerable.Range(1, channels).Select(c => $"0.04*sin(2*PI*{220 * c}*t)"));
                var args = new List<string> { "-v", "error", "-f", "lavfi", "-i", $"aevalsrc={expression}:s={rate}:c={layout}",
                    "-t", "0.4", "-c:a", codec };
                if (track is not null) args.AddRange(["-metadata", "album=Folder album", "-metadata", $"title=Disc {disc} Track {track}",
                    "-metadata", $"artist=Artist {track}", "-metadata", $"{(aliases ? "tracknumber" : "track")}={track}",
                    "-metadata", $"{(aliases ? "discnumber" : "disc")}={disc}"]);
                args.AddRange(["-y", file]); await runner.RunAsync(tools.Ffmpeg, args, CancellationToken.None); return file;
            }
            var one = await Generate(tagged, "z-one.flac", "flac", 88200, 6, "1/10", "1/2");
            var two = await Generate(tagged, "a-two.m4a", "alac", 48000, 2, "2/10", "1/2");
            var ten = await Generate(tagged, "c-ten.mp3", "libmp3lame", 44100, 2, "10/10", "1/2");
            var discTwo = await Generate(tagged, "b-disc-two.flac", "flac", 44100, 1, "1", "2", aliases: true);
            File.WriteAllText(Path.Combine(tagged, "broken.flac"), "not a FLAC");
            File.WriteAllText(Path.Combine(tagged, "notes.txt"), "ignored");
            var nested = Path.Combine(tagged, "Other album"); Directory.CreateDirectory(nested);
            File.Copy(one, Path.Combine(nested, "nested.flac"), true);
            await Generate(natural, "Song 10.wav", "pcm_s16le", 44100, 2);
            await Generate(natural, "Song 2.FLAC", "flac", 44100, 2);
            await Generate(natural, "Song 1.aiff", "pcm_s16be", 44100, 2);

            var album = await service.AnalyzeAsync(tagged, CancellationToken.None); var playlist = album.Playlists.Single();
            var result = await service.AnalyzePlaylistAsync(album, playlist, CancellationToken.None);
            Check(playlist.IsAudioFolder && playlist.Clips.Select(c => c.File!.Path).SequenceEqual(new[] { one, two, ten, discTwo }),
                "Mixed FLAC, ALAC and MP3 files share one list sorted by disc/track tags, including N/total and tag aliases");
            Check(result.Tracks.Count == 4 && result.Tracks[1].Artist == "Artist 2/10" && playlist.SkippedAudioFiles == 1,
                "Per-file names/artists survive; invalid audio is reported and unrelated/nested files are excluded");
            var plain = await service.AnalyzeAsync(natural, CancellationToken.None);
            Check(plain.Playlists.Single().Clips.Select(c => Path.GetFileName(c.File!.Path)).SequenceEqual(new[] { "Song 1.aiff", "Song 2.FLAC", "Song 10.wav" }),
                "Tag-free mixed-format files use natural filename order (1, 2, 10)");
            Check(NaturalFileNameComparer.Instance.Compare("1" + new string('0', 40), "9" + new string('0', 39)) > 0,
                "Natural ordering handles numeric names beyond integer limits");
            var empty = Path.Combine(root, "Empty"); Directory.CreateDirectory(empty);
            try { await service.AnalyzeAsync(empty, CancellationToken.None); throw new Exception("Empty folder accepted"); }
            catch (InvalidDataException) { Check(true, "Empty music folders give a readable error"); }
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                try { await service.AnalyzeAsync(tagged, canceled.Token); throw new Exception("Cancellation ignored"); }
                catch (OperationCanceledException) { Check(true, "Folder opening respects cancellation"); }
            }

            var model = new MainViewModel(new TrackEditsStore(log, Path.Combine(root, "metadata")));
            var window = new MainWindow(model) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            var loaded = typeof(MainWindow).GetMethod("Window_Loaded", BindingFlags.NonPublic | BindingFlags.Instance)!;
            window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window, loaded);
            window.Show();
            var previousFactory = AudioNavigationService.HeadphoneOutputFactory;
            var previousDriver = Environment.GetEnvironmentVariable("SDL_AUDIODRIVER");
            Environment.SetEnvironmentVariable("SDL_AUDIODRIVER", "dummy");
            var options = HeadphoneSession.Current.Options;
            var played = new List<(string Title, int Channels, int Rate)>(); var devices = new List<FastOutput>();
            PropertyChangedEventHandler record = (_, e) =>
            {
                if ((e.PropertyName == nameof(model.IsPlaying) || e.PropertyName == nameof(model.SelectedTrackTitle)) && model.IsPlaying)
                    played.Add((model.SelectedTrack!.Title, model.SelectedStream!.Channels, model.SelectedStream.SampleRate));
            };
            try
            {
                model.ContinuousPlayback = false;
                model.Sources.Add(tagged);
                await model.OpenSourceAsync(tagged);
                Check(model.CanPlay && model.Tracks.Count == 4 && model.ContinuousPlayback && model.Streams.Count == 1,
                    "Opening a folder in the real GUI model selects the first track and enables sequential Play");
                model.PropertyChanged += record;
                AudioNavigationService.HeadphoneOutputFactory = () => { var device = new FastOutput(); devices.Add(device); return device; };
                await model.TogglePlaybackAsync();
                await Until(() => played.Count == 4 && !model.IsPlaying && devices.All(d => d.Disposed));
                Check(played.Select(p => p.Title).SequenceEqual(result.Tracks.Select(t => t.Title)) &&
                    played.Select(p => p.Channels).SequenceEqual(new[] { 6, 2, 2, 1 }) &&
                    played.Select(p => p.Rate).SequenceEqual(new[] { 88200, 48000, 44100, 44100 }) &&
                    model.Status == LanguageService.T("再生が終わりました"),
                    "One Play traverses the folder in order, switching codec/rate/layout for each track and stopping at the end");
                Check(devices.Count == 2 && devices.All(d => d.Peak > .001), "Compatible stereo files share one non-silent native output; mono finishes through the existing dummy FFplay path");
                model.SelectedTrack = model.Tracks[0];
                await model.NextTrackAsync(); await model.PreviousTrackAsync();
                Check(model.SelectedTrack == model.Tracks[0] && model.SelectedStream!.Channels == 6,
                    "Previous/Next select tracks and restore each file's audio format");
                model.ContinuousPlayback = false; played.Clear(); devices.Clear();
                await model.TogglePlaybackAsync(); await Until(() => devices.Count == 1 && devices[0].Disposed && !model.IsPlaying);
                Check(played.Count == 1 && model.SelectedTrack == model.Tracks[0], "Turning Continuous play off still stops after one track");
                model.OutputFolder = Path.Combine(root, "Exports"); model.GroupByChapter = false;
                Check(await model.ConvertAsync(), "Completed folder FLAC export returns success for the completion dialog");
                var saved = Directory.GetFiles(model.SavedFolder, "*.flac").Order().ToArray();
                var formats = new List<AudioStreamInfo>();
                foreach (var file in saved) formats.Add(await probe.ProbeFileAsync(file, CancellationToken.None));
                Check(formats.Count == 4 && formats.Select(s => s.Channels).SequenceEqual(new[] { 6, 2, 2, 1 }) &&
                    formats.All(s => s.SampleRate == 44100 && s.BitDepth == 16),
                    "Folder export preserves each file's own channel layout at the selected CD quality");
                foreach (var track in model.Tracks) track.IsSelected = false;
                Check(!await model.ConvertAsync(), "No selection does not signal export completion");
                foreach (var track in model.Tracks) track.IsSelected = true;
                var blocker = Path.Combine(root, "not-a-directory"); File.WriteAllText(blocker, "block output"); model.OutputFolder = blocker;
                Check(!await model.ConvertAsync() && !model.IsBusy && model.SavedFolder == "", "Failed export does not signal completion or leave the UI busy");
                model.OutputFolder = Path.Combine(root, "Canceled export");
                var conversion = model.ConvertAsync(); model.Cancel();
                Check(!await conversion && !model.IsBusy, "Canceled export does not signal completion");
                model.OutputFolder = Path.Combine(root, "Dialog export");
                foreach (var track in model.Tracks) track.IsSelected = track == model.Tracks[0];
                using (var dialog = new CompletionDialogProbe(window))
                {
                    typeof(MainWindow).GetMethod("Save_Click", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .Invoke(window, [window, new RoutedEventArgs()]);
                    await Until(() => dialog.Messages.Count == 1);
                    Check(dialog.Messages[0].Contains(LanguageService.T("FLACの書き出しが完了しました。")),
                        "Real main-window FLAC export shows the localized completion dialog");
                }
                var mixer = new MixerWindow(model) { Owner = window, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
                mixer.Show();
                try
                {
                    using var dialog = new CompletionDialogProbe(mixer, window);
                    typeof(MixerWindow).GetMethod("Export_Click", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .Invoke(mixer, [mixer, new RoutedEventArgs()]);
                    await Until(() => dialog.Messages.Count == 1);
                    Check(dialog.Messages[0].Contains(LanguageService.T("FLACの書き出しが完了しました。")),
                        "Real mixer FLAC export also shows the completion dialog");
                }
                finally { mixer.Close(); }
                model.Tracks[0].Title = "Saved folder title";
                model.Sources.Add(natural);
                var selector = (System.Windows.Controls.ComboBox)window.FindName("SourceCombo");
                selector.SelectedItem = natural;
                await Until(() => !model.IsBusy && model.Source == natural);
                selector.SelectedItem = tagged;
                await Until(() => !model.IsBusy && model.Source == tagged);
                Check(model.Tracks[0].Title == "Saved folder title", "Folder reopening restores its saved track edits");
                await model.DetectInsertedDiscAsync();
                Check(model.Source == tagged, "Optical-disc polling does not replace the open music folder");
            }
            finally
            {
                model.PropertyChanged -= record; model.Cancel(); model.DetachLanguage(); model.Visualization.Dispose();
                AudioNavigationService.HeadphoneOutputFactory = previousFactory; HeadphoneSession.Current.SetOptions(options);
                Environment.SetEnvironmentVariable("SDL_AUDIODRIVER", previousDriver);
                window.Close();
            }
            File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    // Inspect and dismiss only message boxes owned by this test's window on its UI thread.
    internal sealed class CompletionDialogProbe : IDisposable
    {
        public List<string> Messages { get; } = [];
        readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(25) };
        readonly HashSet<IntPtr> seen = [];
        public CompletionDialogProbe(params Window[] owners)
        {
            var ownerHandles = owners.Select(owner => new System.Windows.Interop.WindowInteropHelper(owner).Handle).ToHashSet();
            var threadId = GetCurrentThreadId();
            timer.Tick += (_, _) => EnumThreadWindows(threadId, (handle, _) =>
            {
                var name = new StringBuilder(128); GetClassName(handle, name, name.Capacity);
                if (name.ToString() != "#32770" || !ownerHandles.Contains(GetWindow(handle, 4)) || !seen.Add(handle)) return true;
                var message = new StringBuilder();
                IntPtr button = IntPtr.Zero;
                EnumChildWindows(handle, (child, _) =>
                {
                    var text = new StringBuilder(512); GetWindowText(child, text, text.Capacity); message.AppendLine(text.ToString()); return true;
                }, IntPtr.Zero);
                EnumChildWindows(handle, (child, _) =>
                {
                    var kind = new StringBuilder(128); GetClassName(child, kind, kind.Capacity);
                    if (kind.ToString() == "Button") { button = child; return false; } return true;
                }, IntPtr.Zero);
                Messages.Add(message.ToString());
                // Click the real acknowledgment button; Windows can assign it ID 2 rather than IDOK.
                if (button != IntPtr.Zero) PostMessage(button, 0x00F5, IntPtr.Zero, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
            timer.Start();
        }
        public void Dispose() => timer.Stop();
        delegate bool EnumWindow(IntPtr handle, IntPtr param);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint threadId, EnumWindow callback, IntPtr param);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr handle, EnumWindow callback, IntPtr param);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr handle, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr handle, StringBuilder text, int count);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr handle, uint command);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr handle, uint message, IntPtr wparam, IntPtr lparam);
    }

    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed.TotalSeconds > 20) throw new TimeoutException("Folder playback did not complete."); await Task.Delay(5); }
    }
    private static void Pump(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    private sealed class FastOutput : IHeadphoneOutput
    {
        public bool Disposed; public double Peak;
        public long SubmittedFrames { get; private set; }
        public long PlayedFrames => SubmittedFrames;
        public double BufferMilliseconds => 10;
        public double StreamLatencyMilliseconds => 0;
        public bool HasStarted => SubmittedFrames > 0;
        public long EmptyBufferCount => 0;
        public bool SupportsPause => true;
        public void Pause() { }
        public void Resume() { }
        public void WaitForSpace(int frames, CancellationToken token) => token.ThrowIfCancellationRequested();
        public void Write(float[] samples, int frames)
        {
            for (int i = 0; i < frames * 2; i++) Peak = Math.Max(Peak, Math.Abs(samples[i]));
            SubmittedFrames += frames;
        }
        public void Drain(CancellationToken token) => token.ThrowIfCancellationRequested();
        public void Dispose() => Disposed = true;
    }
}
