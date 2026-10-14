using Disc2Flac;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

internal static class CacheStorageTests
{
    public static async Task RunAsync(string output)
    {
        var root = Path.GetFullPath(output); Directory.CreateDirectory(root);
        var paths = new AppDataPaths(Path.Combine(root, "appdata"));
        var home = Path.Combine(root, "SpaceSketch");
        var inUse = false;
        var store = new CacheStorage(paths, home, () => inUse);
        void Write(string file, int bytes)
        { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllBytes(file, Enumerable.Repeat((byte)7, bytes).ToArray()); }
        var kept = new[] { Path.Combine(paths.TrackEdits, "disc", "album.json"),
            paths.PreferencePath("sacd-tool.json"), paths.PreferencePath("language.txt"),
            Path.Combine(home, "ViewerSettings.json"), Path.Combine(home, "AudioVisualizationSettings.json"),
            Path.Combine(paths.Root, "logs", "test.log"), Path.Combine(paths.Root, "tools", "id", "ffmpeg.exe"),
            Path.Combine(root, "exports", "saved.flac") };
        foreach (var file in kept) Write(file, 31);
        var audio = Path.Combine(store.AudioPath, "disc", "pcm-88200-24-v1", "mch", "001.flac");
        Write(audio, 2048);
        Write(Path.Combine(store.AudioPath, ".toc-orphan", "temporary.txt"), 128);
        Write(Path.Combine(store.AudioPath, "disc", ".extract-orphan", "temporary.dsf"), 1024);
        Write(Path.Combine(store.ViewerPath, "browser", "Cache", "data.bin"), 512);
        var initial = await store.MeasureAsync();
        Check(initial.AudioBytes == 3200 && initial.ViewerBytes == 512 && initial.ToolBytes == 31 && initial.ScanErrors == 0,
            "Accurate cache totals; settings, logs and exported FLAC excluded");
        inUse = true;
        try { await store.ClearAsync(); throw new Exception("Active cache cleared"); } catch (InvalidOperationException) { }
        Check(File.Exists(audio), "Running player/viewer blocks cache deletion");
        inUse = false;
        var cleared = await store.ClearAsync();
        Check(cleared.DeletedBytes == 3712 && cleared.Errors.Count == 0 && cleared.Usage.ClearableBytes == 0,
            "Audio, browser and orphan job files cleared; zero bytes reported");
        foreach (var file in kept) Check(File.ReadAllBytes(file).Length == 31, "Preserved " + Path.GetFileName(file));
        Write(audio, 100);
        using (var locked = new FileStream(audio, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var partial = await store.ClearAsync();
            Check(partial.Errors.Count > 0 && partial.Usage.AudioBytes == 100 && File.Exists(audio),
                "Locked files are reported and remain measurable");
        }
        Check((await store.ClearAsync()).Usage.ClearableBytes == 0, "Clearing succeeds after file locks are released");
        Write(audio, 100);
        var link = Path.Combine(store.AudioPath, "settings-link");
        var target = Path.GetDirectoryName(kept[0])!;
        CreateJunction(link, target);
        var guarded = await store.ClearAsync();
        Check(guarded.Errors.Count > 0 && File.Exists(audio) && File.ReadAllBytes(kept[0]).Length == 31,
            "Junctions reject deletion before touching the linked tree or any audio file");
        // Remove only the test junction itself; its target is preserved.
        Check((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "Fixture is a junction");
        Directory.Delete(link, recursive: false);
        Check(File.Exists(kept[0]), "Unlinking the fixture leaves its target intact");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? main = null;
            try
            {
                var app = new App(); app.InitializeComponent();
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var model = new MainViewModel(cacheStorage: store);
                main = new MainWindow(model);
                var content = (FrameworkElement)main.Content;
                content.Measure(new Size(1140, 850)); content.Arrange(new Rect(0, 0, 1140, 850));
                Pump(model.RefreshCacheSizeAsync());
                var size = (TextBlock)main.FindName("CacheSizeLabel");
                var clearButton = (Button)main.FindName("ClearCacheButton");
                Check(size.Text == "100 B" && model.CanClearCache, "WPF cache size binding and available clear control");
                typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsPlaying))!.SetValue(model, true);
                Check(!model.CanClearCache, "Playing disables clearing");
                clearButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(Task.CompletedTask);
                Check(File.Exists(audio), "A clear-button request during playback leaves audio intact");
                typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsPlaying))!.SetValue(model, false);
                clearButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var wait = Stopwatch.StartNew();
                while ((model.IsBusy || size.Text != "0 B") && wait.Elapsed < TimeSpan.FromSeconds(10)) Pump(Task.CompletedTask);
                Check(size.Text == "0 B" && !Directory.Exists(store.AudioPath), "The actual clear button deletes cache and updates the on-screen size");
                model.SelectedStream = new() { Index = 0, Codec = "pcm_bluray", SampleRate = 48000, BitDepth = 16, Channels = 6, ChannelLayout = "5.1" };
                var multichannel = (CheckBox)main.FindName("MultichannelFlacCheck");
                multichannel.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Pump(Task.CompletedTask);
                Check(model.SaveMultichannelFlac && model.CanChooseHighRes && model.IsCdSelected,
                    "Multichannel option is bound and CD quality remains the default");
                model.IsHighResSelected = true;
                var quality = (OutputQuality)typeof(MainViewModel).GetField("_quality", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
                Check(quality == OutputQuality.OriginalPcm, "Original PCM is available at 16-bit/48kHz without labeling it high resolution");
                model.SaveMixerDownmix = true;
                Check(!model.SaveMultichannelFlac && model.SaveMixerDownmix && model.IsCdSelected, "Mixer and original multichannel export modes cannot conflict");
                model.SaveMultichannelFlac = true; model.SaveIndividualChannels = true;
                Check(!model.SaveMultichannelFlac && model.SaveIndividualChannels, "Individual-channel export clears the multichannel mode");
                foreach (var file in kept) Check(File.ReadAllBytes(file).Length == 31, "UI clear preserves " + Path.GetFileName(file));
                model.DetachLanguage(); model.Visualization.Dispose();
            }
            catch (Exception ex) { failure = ex; }
            finally { main?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw failure;
        Console.WriteLine("Cache storage safety and UI checks passed.");
    }
    private static void Check(bool ok, string label)
    { if (!ok) throw new InvalidDataException(label); Console.WriteLine("PASS " + label); }
    private static void Pump(Task task)
    {
        do
        {
            var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
        } while (!task.IsCompleted);
        task.GetAwaiter().GetResult();
    }
    private static void CreateJunction(string link, string target)
    {
        string Encode(string text) => Convert.ToBase64String(Encoding.Unicode.GetBytes(text));
        var script = $"$ErrorActionPreference='Stop'\n$taskLink=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{Encode(link)}'))\n" +
            $"$taskTarget=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{Encode(target)}'))\n" +
            "New-Item -ItemType Junction -Path $taskLink -Target $taskTarget | Out-Null";
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Encode(script) }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("Cannot create junction fixture: " + error);
    }
}
