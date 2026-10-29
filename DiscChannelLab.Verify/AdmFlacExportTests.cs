using Disc2Flac;
using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

internal static class AdmFlacExportTests
{
    public static void Run(string output, string? realFile)
    {
        string root = Path.GetFullPath(output); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", Path.Combine(root, "profile"));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainViewModel? model = null; MainWindow? main = null;
            try
            {
                var app = new App(); app.InitializeComponent(); WpfTestApplication.SuppressStartupWindow(app);
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var checks = new List<string>(); var errors = new List<Exception>();
                app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception); e.Handled = true; };
                void Check(bool ok, string message) { if (!ok) throw new InvalidDataException(message); checks.Add(message); Console.WriteLine("PASS " + message); }
                async Task Tests()
                {
                    await Task.Yield(); LanguageService.Instance.SetJapanese(false);
                    string raw = Path.Combine(root, "raw-adm.wav"); Create(raw, 96008);
                    string fixture = Path.Combine(root, "fixture-adm.wav");
                    await AdmMetadataExport.WriteAsync(raw, fixture, new("Test album", "Test artist", "Original synthesized fixture",
                        [new(0, "First song", "Artist A"), new(48008, "Final song", "Artist B")]), null, CancellationToken.None);
                    var wave = AdmWave.Open(fixture); byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(fixture));
                    long oldFirst = ConversionService.ToSample(AdmTimeline.ToTicks(48008), 48000);
                    long oldCount = ConversionService.ToSample(AdmTimeline.ToTicks(96008) - AdmTimeline.ToTicks(48008), 48000);
                    Check(oldFirst + oldCount == wave.Scene.DurationSamples + 1, "The fixture reproduces the original one-sample overrun at the last track");
                    for (int n = 0; n < 16; n++)
                    {
                        long middle = 48000 + n, total = 96000 + n;
                        var residue = wave with { Scene = wave.Scene with { DurationSamples = total }, Album = wave.Album! with { Chapters = [new(0, "A"), new(middle, "B")] } };
                        var a = AdmTimeline.Range(residue, 0, AdmTimeline.ToTicks(middle));
                        var b = AdmTimeline.Range(residue, AdmTimeline.ToTicks(middle), AdmTimeline.ToTicks(total));
                        if (a.First != 0 || a.Frames != middle || b.First != middle || a.Frames + b.Frames != total) throw new InvalidDataException("Sample residue mismatch: " + n);
                    }
                    Check(true, "All 16 sample/tick rounding residues retain exact chapter anchors and partition the complete PCM without gaps or overlaps");
                    var unchaptered = AdmTimeline.Range(wave with { Album = null }, 0, AdmTimeline.ToTicks(wave.Scene.DurationSamples));
                    Check(unchaptered.First == 0 && unchaptered.Frames == wave.Scene.DurationSamples,
                        "An ADM without embedded chapters also keeps its exact PCM endpoint");
                    Check(AdmTimeline.SampleAt(wave, 10001) == ConversionService.ToSample(10001, 48000), "User-created non-chapter boundaries keep their nearest-sample position");
                    bool invalid = false; try { AdmTimeline.Range(wave, 0, AdmTimeline.ToTicks(wave.Scene.DurationSamples) + 1); } catch (InvalidDataException) { invalid = true; }
                    Check(invalid, "A genuinely out-of-range interval remains an error rather than being silently truncated");
                    model = new MainViewModel(); main = new MainWindow(model) { ShowActivated = false };
                    main.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), main, typeof(MainWindow).GetMethod("Window_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)!);
                    main.Show(); await model.OpenAudioFilesAsync([fixture]); await model.EnableAdmPlaybackAsync();
                    var jsonCheck = (CheckBox)main.FindName("SpatialRenderJsonCheck");
                    Check(model.Tracks.Count == 2 && !model.SaveSpatialRenderJson && jsonCheck.IsChecked == false && jsonCheck.IsEnabled,
                        "The real main window opens ADM with an available, unchecked JSON export option");
                    var spans = new List<SpatialInput>();
                    foreach (var track in model.Tracks)
                    {
                        model.SelectedTrack = track;
                        spans.Add(await model.PrepareSpatialInputAsync(0, track.DurationSeconds, CancellationToken.None));
                    }
                    Check(spans[0].FirstSample == 0 && spans[0].Frames == 48008 && spans[1].FirstSample == 48008 && spans[1].Frames == 48000,
                        "The production ADM preparation path restores both exact sample ranges, including the final song");
                    var originalDisc = await new AudioFileReader(new FfprobeService(new ToolPaths(), new ProcessRunner(new AppLog())), new AppLog()).AnalyzeAsync([fixture], CancellationToken.None);
                    var retained = AdmMetadataExport.FromTracks(originalDisc, originalDisc.Playlists[0], model.Tracks.ToArray());
                    Check(retained.Chapters[1].StartSample == 48008, "Re-exporting ADM track information retains exact chapter samples after the same UI tick conversion");
                    model.OutputFolder = Path.Combine(root, "cd-no-json"); model.IsCdSelected = true;
                    Check(await model.ConvertAsync(), "Whole selected ADM exports successfully through the real FLAC command at CD quality");
                    string[] cd = Directory.GetFiles(model.OutputFolder, "*.flac", SearchOption.AllDirectories).Order().ToArray();
                    Check(cd.Length == 2 && !Directory.GetFiles(model.OutputFolder, "*.json", SearchOption.AllDirectories).Any(), "Default export produces every selected FLAC and no JSON files");
                    var tools = new ToolPaths(); var log = new AppLog(); var probe = new FfprobeService(tools, new ProcessRunner(log));
                    var final = await probe.ProbeAudioFileAsync(cd[1], CancellationToken.None);
                    Check(final.Analysis.Streams[0] is { Channels: 2, SampleRate: 44100, BitDepth: 16, SampleCount: 44100 } &&
                        final.Analysis.Tags.GetValueOrDefault("title") == "Final song" && final.Analysis.Tags.GetValueOrDefault("artist") == "Artist B",
                        "The final CD FLAC has the exact one-second PCM length, stereo format and entered song/artist tags");
                    jsonCheck.IsChecked = true;
                    Check(model.SaveSpatialRenderJson, "Checking the visible JSON option reaches the real export setting");
                    model.OutputFolder = Path.Combine(root, "hires-with-json"); model.IsHighResSelected = true;
                    Check(await model.ConvertAsync(), "Opt-in JSON export also completes at 24-bit / 48 kHz");
                    string[] high = Directory.GetFiles(model.OutputFolder, "*.flac", SearchOption.AllDirectories).Order().ToArray();
                    using (var json = JsonDocument.Parse(File.ReadAllText(high[1] + ".render.json")))
                        Check(high.Length == 2 && Directory.GetFiles(model.OutputFolder, "*.render.json", SearchOption.AllDirectories).Length == 2 &&
                            json.RootElement.GetProperty("SourceFrames").GetInt64() == 48000 && json.RootElement.GetProperty("Input").GetProperty("FirstSample").GetInt64() == 48008,
                            "Opt-in sidecars retain exact sample counts and the HRTF/mixer render snapshot");
                    var highData = await probe.ProbeAudioFileAsync(high[1], CancellationToken.None);
                    Check(highData.Analysis.Streams[0] is { Channels: 2, SampleRate: 48000, BitDepth: 24, SampleCount: 48000 },
                        "The final high-resolution FLAC has exactly the original requested 48000 samples");
                    jsonCheck.IsChecked = false; LanguageService.Instance.SetJapanese(true);
                    Check(!model.SaveSpatialRenderJson && jsonCheck.Content.ToString()!.Contains("JSONにも保存"), "The JSON control can be turned off and switches correctly to Japanese");
                    var snapshot = new SpatialRenderSnapshot(new() { Enabled = true, Method = HeadphoneMethod.SteamAudio }, new(0, 0, 0),
                        new(StereoMixSettings.Default, null), 1, HrtfEntry.BuiltIn.Hash);
                    var audio = await SpatialRendering.RenderAsync(spans[1], snapshot, false, CancellationToken.None);
                    string stale = Path.Combine(root, "stale-sidecar.flac"); File.WriteAllText(stale + ".render.json", "Keep this existing user file");
                    await SpatialRendering.ExportAsync(audio, stale, tools.Ffmpeg, true, CancellationToken.None);
                    Check(File.Exists(stale) && File.ReadAllText(stale + ".render.json") == "Keep this existing user file", "With JSON disabled an existing orphan sidecar neither blocks audio export nor gets overwritten");
                    string blocked = Path.Combine(root, "existing-sidecar.flac"); File.WriteAllText(blocked + ".render.json", "Keep"); bool refused = false;
                    try { await SpatialRendering.ExportAsync(audio, blocked, tools.Ffmpeg, true, CancellationToken.None, writeRenderJson: true); } catch (IOException) { refused = true; }
                    Check(refused && !File.Exists(blocked) && File.ReadAllText(blocked + ".render.json") == "Keep", "Opt-in JSON never overwrites an existing sidecar");
                    string rollback = Path.Combine(root, "json-failure.flac"); Directory.CreateDirectory(rollback + ".render.json");
                    File.WriteAllText(Path.Combine(rollback + ".render.json", "keep.txt"), "Keep"); bool failed = false;
                    try { await SpatialRendering.ExportAsync(audio, rollback, tools.Ffmpeg, true, CancellationToken.None, writeRenderJson: true); } catch (IOException) { failed = true; }
                    Check(failed && !File.Exists(rollback) && File.ReadAllText(Path.Combine(rollback + ".render.json", "keep.txt")) == "Keep",
                        "A sidecar publication failure rolls back only the newly generated audio and preserves the conflicting user directory");
                    string cancel = Path.Combine(root, "canceled.flac"); using var stopped = new CancellationTokenSource(); stopped.Cancel(); bool canceled = false;
                    try { await SpatialRendering.ExportAsync(audio, cancel, tools.Ffmpeg, true, stopped.Token, writeRenderJson: true); } catch (OperationCanceledException) { canceled = true; }
                    Check(canceled && !File.Exists(cancel) && !File.Exists(cancel + ".render.json") && !Directory.GetFiles(root, "*.partial*", SearchOption.AllDirectories).Any(),
                        "Cancellation leaves no audio, sidecar or private staging files");
                    Check(sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(fixture))), "ADM input audio, position data and embedded track information remain byte-for-byte unchanged");
                    if (realFile is not null)
                    {
                        string source = Path.GetFullPath(realFile); var info = new FileInfo(source); long bytes = info.Length, stamp = info.LastWriteTimeUtc.Ticks;
                        await model.OpenAudioFilesAsync([source]); await model.EnableAdmPlaybackAsync();
                        var actual = model.CurrentAdm!; long at = 0;
                        foreach (var track in model.Tracks)
                        {
                            model.SelectedTrack = track; var range = await model.PrepareSpatialInputAsync(0, track.DurationSeconds, CancellationToken.None);
                            if (range.FirstSample != at) throw new InvalidDataException("Real-source chapter gap or overlap."); at += range.Frames;
                        }
                        Check(at == actual.Scene.DurationSamples, "Every real-source chapter covers the original PCM exactly with no missing or duplicated samples");
                        foreach (var track in model.Tracks) track.IsSelected = ReferenceEquals(track, model.SelectedTrack);
                        model.SaveSpatialRenderJson = false; model.IsHighResSelected = true; model.OutputFolder = Path.Combine(root, "real-final-track");
                        Check(await model.ConvertAsync(), "The previously failing real final track completes full HRTF FLAC rendering");
                        string saved = Directory.GetFiles(model.OutputFolder, "*.flac", SearchOption.AllDirectories).Single();
                        var savedData = await probe.ProbeAudioFileAsync(saved, CancellationToken.None);
                        long expected = actual.Scene.DurationSamples - actual.Album!.Chapters[^1].StartSample;
                        Check(savedData.Analysis.Streams[0].SampleCount == expected && !File.Exists(saved + ".render.json"),
                            "The real final FLAC retains every requested source sample at 48 kHz and creates no JSON by default");
                        var after = new FileInfo(source); Check(after.Length == bytes && after.LastWriteTimeUtc.Ticks == stamp,
                            "The full-size user ADM source was opened read-only and remains unchanged");
                    }
                    Check(errors.Count == 0, "No WPF dispatcher exceptions during ADM preparation, option changes and exports");
                    SpatialJson.Save(Path.Combine(root, "results.json"), new { passed = checks.Count, checks, HumanListening = false });
                }
                var task = Tests(); var frame = new DispatcherFrame();
                _ = task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
                if (!task.IsCompleted) Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
            }
            catch (Exception e) { failure = e; }
            finally { model?.StopPlayback(); model?.Cancel(); model?.DetachLanguage(); model?.Visualization.Dispose(); main?.Close(); Application.Current?.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); if (failure is not null) throw failure;
    }

    static void Create(string path, int frames)
    {
        string duration = DamfImporter.Time(frames);
        string xml = $$"""
        <ebuCoreMain xmlns="urn:ebu:metadata-schema:ebuCore_2017"><coreMetadata><format><audioFormatExtended version="ITU-R_BS.2076-2">
        <audioProgramme audioProgrammeID="APR_1001" audioProgrammeName="Synthesized rounding test"><audioContentIDRef>ACO_1001</audioContentIDRef></audioProgramme>
        <audioContent audioContentID="ACO_1001"><audioObjectIDRef>AO_1001</audioObjectIDRef></audioContent>
        <audioObject audioObjectID="AO_1001" audioObjectName="Original tone" start="00:00:00" duration="{{duration}}"><audioPackFormatIDRef>AP_00031001</audioPackFormatIDRef><audioTrackUIDRef>ATU_00000001</audioTrackUIDRef></audioObject>
        <audioPackFormat audioPackFormatID="AP_00031001" typeDefinition="Objects" typeLabel="0003"><audioChannelFormatIDRef>AC_00031001</audioChannelFormatIDRef></audioPackFormat>
        <audioChannelFormat audioChannelFormatID="AC_00031001" typeDefinition="Objects" typeLabel="0003"><audioBlockFormat audioBlockFormatID="AB_00031001_00000001" rtime="00:00:00" duration="{{duration}}"><position coordinate="azimuth">0</position><position coordinate="elevation">0</position><jumpPosition>1</jumpPosition></audioBlockFormat></audioChannelFormat>
        <audioStreamFormat audioStreamFormatID="AS_00031001" formatDefinition="PCM" formatLabel="0001"><audioChannelFormatIDRef>AC_00031001</audioChannelFormatIDRef><audioTrackFormatIDRef>AT_00031001_01</audioTrackFormatIDRef></audioStreamFormat>
        <audioTrackFormat audioTrackFormatID="AT_00031001_01" formatDefinition="PCM" formatLabel="0001"><audioStreamFormatIDRef>AS_00031001</audioStreamFormatIDRef></audioTrackFormat>
        <audioTrackUID UID="ATU_00000001"><audioTrackFormatIDRef>AT_00031001_01</audioTrackFormatIDRef><audioPackFormatIDRef>AP_00031001</audioPackFormatIDRef></audioTrackUID>
        </audioFormatExtended></format></coreMetadata></ebuCoreMain>
        """;
        using var f = new FileStream(path, FileMode.CreateNew); using var w = new BinaryWriter(f);
        w.Write("RIFF"u8); w.Write(0u); w.Write("WAVE"u8);
        void Chunk(string id, byte[] data) { w.Write(Encoding.ASCII.GetBytes(id)); w.Write((uint)data.Length); w.Write(data); if ((data.Length & 1) != 0) w.Write((byte)0); }
        var fmt = new byte[16]; BinaryPrimitives.WriteUInt16LittleEndian(fmt, 3); BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(fmt.AsSpan(4), 48000); BinaryPrimitives.WriteInt32LittleEndian(fmt.AsSpan(8), 48000 * 4);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), 4); BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), 32); Chunk("fmt ", fmt);
        Chunk("axml", Encoding.UTF8.GetBytes(xml));
        using var chna = new MemoryStream(); using (var c = new BinaryWriter(chna, Encoding.ASCII, true))
        { c.Write((ushort)1); c.Write((ushort)1); c.Write((ushort)1); c.Write("ATU_00000001"u8); c.Write("AT_00031001_01"u8); c.Write("AP_00031001"u8); c.Write((byte)0); }
        Chunk("chna", chna.ToArray()); w.Write("data"u8); w.Write((uint)(frames * 4));
        for (int i = 0; i < frames; i++) w.Write((float)(.04 * Math.Sin(i * 2 * Math.PI * 880 / 48000)));
        long length = f.Length; f.Position = 4; w.Write((uint)(length - 8));
    }
}
