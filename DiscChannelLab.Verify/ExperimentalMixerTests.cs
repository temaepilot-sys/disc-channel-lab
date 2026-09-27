using Disc2Flac;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class ExperimentalMixerTests
{
    public static async Task RunAsync(string output)
    {
        // Independent channels must have unity gain at 100%, regardless of layout.
        var layouts = (IReadOnlyDictionary<string, string>)typeof(StereoMixSettings)
            .GetField("Layouts", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        foreach (var layout in layouts)
        {
            var names = layout.Value.Split(' ');
            var stream = Stream(names.Length, layout.Key);
            var model = new MainViewModel();
            model.SelectedStream = stream;
            var afterL = new double[names.Length]; var afterR = new double[names.Length];
            foreach (var strip in model.MixerStrips) strip.Level = 100;
            StereoPreviewMixer.BuildWeights(names, new(StereoMixSettings.Default, null,
                model.MixerStrips.Select(x => x.Snapshot()).ToArray()), afterL, afterR);
            for (var i = 0; i < names.Length; i++)
            {
                var pan = model.MixerStrips[i].DefaultPan;
                var expectedL = pan < 0 ? 1 : pan > 0 ? 0 : Math.Sqrt(.5);
                var expectedR = pan > 0 ? 1 : pan < 0 ? 0 : Math.Sqrt(.5);
                Check(Math.Abs(expectedL - afterL[i]) < 1e-10 && Math.Abs(expectedR - afterR[i]) < 1e-10,
                    $"Unity gain/pan mismatch for {layout.Key}/{names[i]}");
            }
            model.DetachLanguage();
        }
        Console.WriteLine($"Unity gain and equal-power pan verified for all {layouts.Count} supported layouts.");

        var audio = Stream(6, "5.1");
        var channelNames = StereoMixSettings.ChannelNames(audio);
        var unityInput = new byte[4800 * 12];
        for (var i = 0; i < 4800; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(unityInput.AsSpan(i * 12, 2), (short)(i % 2 == 0 ? 12000 : -8000));
            BinaryPrimitives.WriteInt16LittleEndian(unityInput.AsSpan(i * 12 + 2, 2), (short)(i % 3 == 0 ? -9000 : 4000));
        }
        var unityState = new PreviewMixState(StereoMixSettings.Default, null,
            channelNames.Select(code => new ExperimentalChannel(code, 1, code == "FL" ? -1 : code == "FR" ? 1 : 0, false)).ToArray());
        using (var input = new MemoryStream(unityInput))
        using (var outputPcm = new MemoryStream())
        {
            var meters = new List<MixerMeterFrame>();
            await StereoPreviewMixer.CopyAsync(input, outputPcm, audio, () => unityState, () => 1,
                CancellationToken.None, mixedLevels: meters.Add);
            var outputBytes = outputPcm.ToArray();
            for (var i = 0; i < 4800; i++)
                Check(unityInput.AsSpan(i * 12, 4).SequenceEqual(outputBytes.AsSpan(i * 4, 4)),
                    "FL/FR unity faders changed PCM samples");
            Check(meters.Count == 5 && meters.All(m => m.Input[0] == m.Left[0] && m.Input[1] == m.Right[1] &&
                double.IsNegativeInfinity(m.Right[0]) && double.IsNegativeInfinity(m.Left[1])),
                "FL/FR Input and Post meters differ at 100% with original pan");
        }
        Console.WriteLine("FL/FR at 100% with original pan: PCM samples and Input/Post peaks match exactly.");
        // Synthetic FL-only signal: pan it to R, then mute it while PCM is flowing.
        var pcm = new byte[24000 * 12];
        for (var i = 0; i < 24000; i++) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 12, 2), 12000);
        var strips = channelNames.Select(x => new ExperimentalChannel(x, 1, x == "FL" ? 1 : 0, false)).ToArray();
        var state = new PreviewMixState(StereoMixSettings.Default, null, strips);
        var frames = new List<MixerMeterFrame>();
        using var source = new MemoryStream(pcm);
        using var destination = new MemoryStream();
        var task = StereoPreviewMixer.CopyAsync(source, destination, audio, () => Volatile.Read(ref state), () => 1,
            CancellationToken.None, mixedLevels: frame => frames.Add(frame));
        await Task.Delay(160);
        Volatile.Write(ref state, state with { Channels = strips.Select(x => x with { Muted = true }).ToArray() });
        await task;
        var result = destination.ToArray();
        Check(BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(2400 * 4, 2)) == 0, "Pan leaked to left output");
        var sample = BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(2400 * 4 + 2, 2));
        Check(sample == 12000, "100% fader changed the input amplitude");
        Check(frames[2].Input[0] == frames[2].Right[0], "Input and Post peaks differ at unity gain");
        Check(BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(22000 * 4 + 2, 2)) == 0, "Live mute did not silence PCM");
        var first = frames[2];
        Check(double.IsNegativeInfinity(first.Left[0]) && Math.Abs(first.Right[0] - 20 * Math.Log10(sample / 32768d)) < .01,
            "Post meters do not match rendered PCM");
        Check(frames.Count == 25 && double.IsNegativeInfinity(frames[^1].Output[1]), "Meter timing/mute incorrect");
        Check(Math.Abs(frames[0].Seconds - .01) < 1e-9 && Math.Abs(frames[^1].Seconds - .49) < 1e-9, "Meter timestamps incorrect");
        Console.WriteLine("Live pan, live mute, post/output peaks and sample timestamps verified against PCM.");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { VerifyUi(output); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }

    private static void VerifyUi(string output)
    {
        Directory.CreateDirectory(output);
        var app = new App(); app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var errors = new CaptureTrace();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        var model = new MainViewModel();
        model.SelectedStream = Stream(5, "5.0");
        model.ExperimentalMixerEnabled = true;
        Check(model.MixerStrips.Count == 5 && model.MixerStrips.All(x => x.Code != "LFE"), "5.0 has a phantom LFE");
        var window = new MixerWindow(model);
        var root = (FrameworkElement)window.Content;
        ThemeService.Apply(true); LanguageService.Instance.Apply(false);
        Layout(root, 1200, 760);
        Pump();
        var firstFader = Descendants<Slider>(root).First(x => x.DataContext is MixerStrip && x.Orientation == Orientation.Vertical);
        var firstStrip = (MixerStrip)firstFader.DataContext;
        Check(firstFader.Maximum == 100, "Channel fader exceeds 100%");
        firstFader.Value = 50;
        Pump();
        Check(Math.Abs(firstStrip.Level - 25) < 1e-8, "Fader listening curve binding failed");
        var pan = Descendants<Slider>(root).First(x => x.DataContext == firstStrip && x.Orientation == Orientation.Horizontal);
        pan.Value = 100; Pump();
        Check(firstStrip.Pan == 100, "Pan binding failed");
        var mute = Descendants<CheckBox>(root).First(x => x.DataContext == firstStrip);
        mute.IsChecked = true; Pump();
        Check(firstStrip.Muted, "Mute binding failed");
        var level = Descendants<TextBox>(root).First(x => x.DataContext == firstStrip);
        level.Text = "75"; level.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); Pump();
        Check(firstStrip.Level == 75, "Numeric input binding failed");
        level.Text = "100.01"; level.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); Pump();
        Check(Validation.GetHasError(level) && firstStrip.Level == 75, "Invalid gain was accepted");
        Check(!window.CommitInputs(), "Invalid field did not block export");
        level.GetBindingExpression(TextBox.TextProperty)!.UpdateTarget();
        model.ResetExperimentalMixer(); Pump();
        Check(firstStrip.Pan == -100 && firstStrip.Level == 100 && !firstStrip.Muted, "Reset failed");
        VerifyPresets(model, output);
        Check(model.MixerStrips.Single(x => x.Code == "FC").Pan == 0 && model.MixerStrips.Single(x => x.Code == "FR").Pan == 100,
            "Default pan positions incorrect");
        model.SaveMixerDownmix = true;
        Check(model.SaveMixerDownmix && !model.SaveStereoDownmix && !model.SaveIndividualChannels, "Mixer export mode conflict");
        model.SaveStereoDownmix = true;
        Check(!model.SaveMixerDownmix && model.SaveStereoDownmix, "Group mix mode did not clear mixer export");
        model.SaveMixerDownmix = true;
        model.SaveIndividualChannels = true;
        Check(!model.SaveMixerDownmix && model.SaveIndividualChannels, "Individual export mode did not clear mixer export");
        model.ExperimentalMixerEnabled = false;
        var live = (PreviewMixState)typeof(MainViewModel).GetField("_livePreviewMix", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
        Check(live.Channels is null, "Bypass did not restore original mixer");
        model.ExperimentalMixerEnabled = true;
        for (var i = 0; i < model.MixerStrips.Count; i++)
        {
            model.MixerStrips[i].Input.Update(-8 - i * 3, -20);
            model.MixerStrips[i].PostLeft.Update(-10 - i * 3, -20);
            model.MixerStrips[i].PostRight.Update(-18 - i * 2, -25);
        }
        model.MixerOutputLeft.Update(-4, -15); model.MixerOutputRight.Update(-6, -17);
        Pump(); Layout(root, 1200, 760);
        Render(root, Path.Combine(output, "mixer-dark-en.png"));
        ThemeService.Apply(false); LanguageService.Instance.Apply(true); Pump(); Layout(root, 744, 700);
        Render(root, Path.Combine(output, "mixer-light-ja-small.png"));
        window.Close();
        Check(model.ExperimentalMixerEnabled && firstStrip.Level == 100, "Closing changed mixer state");
        var reopened = new MixerWindow(model);
        Layout((FrameworkElement)reopened.Content, 1200, 660);
        Check(ReferenceEquals(model, reopened.DataContext), "Reopening lost shared settings");
        reopened.Close(); model.DetachLanguage();
        Check(errors.Errors.Count == 0, "WPF binding errors: " + string.Join("; ", errors.Errors));
        Console.WriteLine("WPF faders, numeric validation, pan, mute, bypass, reset, close/reopen and dark/light layouts verified.");
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    private static void VerifyPresets(MainViewModel model, string output)
    {
        model.MixerVariantName = "Study 日本語";
        model.Volume = 175; model.PerceivedVolume = false; model.IncludeMixerMaster = true;
        var fl = model.MixerStrips.Single(x => x.Code == "FL");
        fl.Level = 37.25; fl.Pan = 42; fl.Muted = true;
        var path = Path.Combine(output, "mixer-preset.json");
        model.CaptureMixerPreset().Save(path);
        var preset = MixerPreset.Load(path);
        // Apply to another disc/stream instance; match channel identity rather than JSON order.
        var other = new MainViewModel { SelectedStream = Stream(5, "5.0") };
        other.ApplyMixerPreset(preset with { Channels = preset.Channels.Reverse().ToArray() });
        var restored = other.MixerStrips.Single(x => x.Code == "FL");
        Check(restored.Level == 37.25 && restored.Pan == 42 && restored.Muted &&
            other.Volume == 175 && !other.PerceivedVolume && other.IncludeMixerMaster &&
            other.MixerVariantName == "Study 日本語" && other.ExperimentalMixerEnabled, "Preset round trip lost settings");
        var live = (PreviewMixState)typeof(MainViewModel).GetField("_livePreviewMix", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(other)!;
        Check(live.Channels!.Single(x => x.Code == "FL") == restored.Snapshot(), "Preset was not applied to playback");
        var before = other.CaptureMixerPreset();
        foreach (var bad in new[]
        {
            preset with { Version = 99 },
            preset with { MasterPercent = 201 },
            preset with { Channels = preset.Channels.Select(c => c with { Gain = 1.01 }).ToArray() },
            preset with { Channels = preset.Channels.Select(c => c with { Pan = double.NaN }).ToArray() },
            preset with { Channels = preset.Channels.Select(c => c with { Code = "FL" }).ToArray() },
            preset with { Channels = preset.Channels.Select(c => c.Code == "FL" ? c with { Code = "LFE" } : c).ToArray() }
        })
        {
            var rejected = false;
            try { other.ApplyMixerPreset(bad); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && other.CaptureMixerPreset().Channels.SequenceEqual(before.Channels) && other.Volume == before.MasterPercent,
                "Invalid or incompatible preset partially changed live settings");
        }
        var malformed = Path.Combine(output, "incomplete-preset.json");
        File.WriteAllText(malformed, File.ReadAllText(path).Replace("\"gain\": 0.3725,", ""));
        var missingRejected = false;
        try { MixerPreset.Load(malformed); } catch (System.Text.Json.JsonException) { missingRejected = true; }
        Check(missingRejected, "Missing channel gain silently became zero");
        other.SelectedStream = null;
        other.SelectedStream = Stream(5, "5.0");
        Check(other.MixerStrips.Single(x => x.Code == "FL").Snapshot() == restored.Snapshot(), "Changing discs lost channel settings");
        other.DetachLanguage();
        model.ResetExperimentalMixer(); model.Volume = 100; model.PerceivedVolume = true; model.IncludeMixerMaster = false;
        Console.WriteLine("Independent JSON preset round trip, channel identity, live application, disc switching and atomic invalid-file rejection verified.");
    }
    private static void Layout(FrameworkElement root, double width, double height)
    { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
    private static void Render(FrameworkElement root, string path)
    {
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth + 32, (int)root.ActualHeight + 32, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
            drawing.DrawRectangle((Brush)Application.Current.FindResource("AppBackgroundBrush"), null,
                new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
        bitmap.Render(background);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (var item in Descendants<T>(child)) yield return item;
        }
    }
    private static AudioStreamInfo Stream(int channels, string layout) => new()
    { Index = 0, Codec = "pcm_s16le", Channels = channels, ChannelLayout = layout, SampleRate = 48000, BitDepth = 16 };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private sealed class CaptureTrace : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (message?.Contains("Error:") == true) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
