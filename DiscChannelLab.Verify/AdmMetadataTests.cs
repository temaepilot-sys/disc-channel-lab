using Disc2Flac;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using System.Xml.Linq;

internal static class AdmMetadataTests
{
    public static void Run(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", Path.Combine(output, "profile"));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainViewModel? model = null;
            try
            {
                var app = new App(); app.InitializeComponent(); WpfTestApplication.SuppressStartupWindow(app);
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var checks = new List<string>(); var errors = new List<Exception>();
                app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception); e.Handled = true; };
                void Check(bool ok, string message) { if (!ok) throw new InvalidDataException(message); checks.Add(message); Console.WriteLine("PASS " + message); }
                async Task Tests()
                {
                    await Task.Yield();
                    string original = Path.Combine(output, "original.wav"); AdmDemo.Create(original);
                    byte[] hash = SHA256.HashData(File.ReadAllBytes(original)); var originalWave = AdmWave.Open(original);
                    model = new MainViewModel(); await model.OpenAudioFilesAsync([original]); await model.EnableAdmPlaybackAsync();
                    // Exercise persisted disc edits via the real UI model before exporting a previously prepared file.
                    Check(model.ApplyBulkEdit(new BulkEditPlan("盤の名前 — Disc", [new(0, "第1曲 ‘光’ / 第一章", true, "Artist α")], [], false, null)
                        { TitleName = "公演名 & Album <2026>" }), "The actual bulk editor accepts disc, title, song and artist edits");
                    model.SelectedTrack = model.Tracks[0]; await model.SeekAsync(4); model.SplitAtPreview();
                    await model.SeekAsync(5); model.SplitAtPreview();
                    Check(model.Tracks.Count == 3, "The export fixture includes three user-edited song boundaries");
                    for (int i = 0; i < model.Tracks.Count; i++) { model.Tracks[i].Title = $"曲 {i + 1} / Track ✨"; model.Tracks[i].Artist = i == 1 ? "Artist β" : "Artist α"; }
                    model.Tracks[1].IsSelected = false;
                    string first = Path.Combine(output, "edited.wav");
                    Check(await model.ExportAdmAsync(first), "Save ADM exports current edits from an existing prepared ADM");
                    var saved = AdmWave.Open(first);
                    Check(saved.Album?.DiscTitle == "盤の名前 — Disc" && saved.Album.Album == "公演名 & Album <2026>" && saved.Programmes[0].Name == saved.Album.Album,
                        "The disc name, album/title name and standard ADM programme name are retained separately");
                    Check(saved.Album!.Chapters.Length == 3 && saved.Album.Chapters.Select(c => c.Title).SequenceEqual(model.Tracks.Select(t => t.Title)) &&
                        saved.Album.Chapters.Select(c => c.Artist).SequenceEqual(model.Tracks.Select(t => t.Artist)),
                        "All song names and per-song artists survive UTF-8, including unselected songs in the whole-title archive");
                    Check(saved.Album.Chapters.Select(c => c.StartSample).SequenceEqual(model.Tracks.Select(t => ConversionService.ToSample(t.StartTicks, 48000))),
                        "User-edited boundaries keep their exact sample positions");
                    Check(Pcm(first).SequenceEqual(Pcm(original)) && Scene(first) == Scene(original) && Chunk(first, "chna").SequenceEqual(Chunk(original, "chna")),
                        "Metadata changes preserve every PCM byte, object position, gain, block time and CHNA link");
                    model.Tracks[0].Title = "再編集 — 最新の曲名"; model.Tracks[0].Artist = "Latest artist";
                    string second = Path.Combine(output, "edited-again.wav");
                    Check(await model.ExportAdmAsync(second) && AdmWave.Open(second).Album!.Chapters[0].Title == model.Tracks[0].Title &&
                        AdmWave.Open(second).Album!.Chapters[0].Artist == "Latest artist", "Later edits override stale prepared/cached metadata without decoding the audio again");
                    Check(SHA256.HashData(File.ReadAllBytes(original)).SequenceEqual(hash), "Both exports leave the original prepared ADM byte-for-byte unchanged");
                    await model.OpenAudioFilesAsync([second]);
                    Check(model.AlbumTitle == "盤の名前 — Disc" && model.TitleName == "公演名 & Album <2026>" && model.Tracks.Count == 3 &&
                        model.Tracks[0].Title == "再編集 — 最新の曲名" && model.Tracks[1].Artist == "Artist β",
                        "Open audio file restores the embedded names, artists and split tracks without a sidecar");
                    var disc = new DiscAnalysis { Root = "E:\\", AlbumTitle = "Disc", Playlists = [], Date = "2026", Genre = "Test", Publisher = "Publisher", Description = "Notes" };
                    var title = new PlaylistInfo { Id = 1, DurationTicks = 12 * 45000, TitleName = "Album", Clips = [], ChapterStarts = [0] };
                    var rows = new[] { new TrackRow { Number = 1, StartTicks = 0, EndTicks = 12 * 45000, IsChapter = true, Title = "Song", Artist = "Shared artist" } };
                    var album = AdmMetadataExport.FromTracks(disc, title, rows);
                    Check(album.Artist == "Shared artist", "A common entered track artist supplies the album artist when the disc has none");
                    string discRoot = Path.Combine(output, "disc"); Directory.CreateDirectory(Path.Combine(discRoot, "BDMV", "STREAM"));
                    File.WriteAllText(Path.Combine(discRoot, "BDMV", "STREAM", "00000.m2ts"), "Source identity fixture; not decoder input");
                    var discTitle = new PlaylistInfo { Id = 1, DurationTicks = 12 * 45000, TitleName = "Latest disc title", Format = DiscFormat.BluRay,
                        Clips = [new("00000", 0, 12 * 45000, 0)], ChapterStarts = [0] };
                    var sourceDisc = new DiscAnalysis { Root = discRoot, DiscKey = new string('B', 64), AlbumTitle = "Disc edits", Playlists = [discTitle] };
                    var stream = new AudioStreamInfo { Index = 1, Codec = "truehd", Profile = "Atmos", SampleRate = 48000, Channels = 8, ChannelLayout = "7.1" };
                    AtmosImportService.RegisterPrepared(sourceDisc, discTitle, stream, first);
                    var tools = new ToolPaths(); var log = new AppLog(); var probe = new FfprobeService(tools, new ProcessRunner(log));
                    string prepared = await new AtmosImportService(tools, probe, log).PrepareAsync(sourceDisc, discTitle, stream, rows, null, CancellationToken.None);
                    Check(prepared == first, "A registered full-title ADM is still reused even after disc metadata changes");
                    var latest = AdmMetadataExport.FromTracks(sourceDisc, discTitle, rows);
                    string cached = Path.Combine(output, "registered-current.wav"); await AdmMetadataExport.WriteAsync(prepared, cached, latest, null, CancellationToken.None);
                    Check(AdmWave.Open(cached).Album!.Album == "Latest disc title" && AdmWave.Open(cached).Album!.Chapters[0].Title == "Song" &&
                        Pcm(cached).SequenceEqual(Pcm(first)), "Disc edits replace registered ADM metadata without requiring the original decoder or changing PCM");
                    string bw64 = Path.Combine(output, "bw64.wav"); MakeBw64(first, bw64);
                    string bw64Edited = Path.Combine(output, "bw64-edited.wav");
                    await AdmMetadataExport.WriteAsync(bw64, bw64Edited, album, null, CancellationToken.None);
                    var full = AdmWave.Open(bw64Edited); var tags = AdmFileFormat.Probe(full).Analysis.Tags;
                    Check(full.Album!.Date == "2026" && tags["genre"] == "Test" && tags["publisher"] == "Publisher" && tags["description"] == "Notes",
                        "Date, genre, publisher and notes also round-trip through the embedded metadata");
                    byte[] file = File.ReadAllBytes(bw64Edited);
                    Check(Encoding.ASCII.GetString(file, 12, 4) == "ds64" && BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(20)) == (ulong)(file.Length - 8) &&
                        BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(28)) == (ulong)full.DataBytes && Pcm(bw64Edited).SequenceEqual(Pcm(original)),
                        "BW64 keeps ds64 first, correct 64-bit lengths and intact audio");
                    Check(Chunk(bw64Edited, "TEST").SequenceEqual(new byte[] { 1, 2, 3, 4, 5 }) && Chunk(bw64Edited, "ds64").Length == 40,
                        "An unrelated odd-sized chunk and its RF64 size-table entry survive metadata updates");
                    string third = Path.Combine(output, "reexport.wav"); await AdmMetadataExport.WriteAsync(bw64Edited, third, album, null, CancellationToken.None);
                    Check(AdmWave.Open(third).Album!.Chapters.Length == 1 && Count(third, "dclc") == 1 && Pcm(third).SequenceEqual(Pcm(original)),
                        "Repeated exports replace chapter metadata once without accumulating stale chunks");
                    byte[] keep = SHA256.HashData(File.ReadAllBytes(third)); bool refused = false;
                    try { await AdmMetadataExport.WriteAsync(first, third, album, null, CancellationToken.None); } catch (IOException) { refused = true; }
                    Check(refused && SHA256.HashData(File.ReadAllBytes(third)).SequenceEqual(keep), "An existing destination is never overwritten");
                    var invalid = album with { Chapters = [new(576000, "Outside")] }; bool rejected = false;
                    try { await AdmMetadataExport.WriteAsync(first, Path.Combine(output, "invalid.wav"), invalid, null, CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
                    Check(rejected && !File.Exists(Path.Combine(output, "invalid.wav")), "Invalid chapter positions cannot publish a completed ADM");
                    string canceled = Path.Combine(output, "canceled.wav"); using var stop = new CancellationTokenSource();
                    var progress = new CancelAfterSave(stop); bool canceledOk = false;
                    try { await AdmMetadataExport.WriteAsync(first, canceled, album, progress, stop.Token); } catch (OperationCanceledException) { canceledOk = true; }
                    Check(canceledOk && !File.Exists(canceled) && !Directory.GetFiles(output, "*.partial").Any(), "Cancellation publishes no WAV and cleans the private partial file");
                    Check(errors.Count == 0, "Real UI export and reopen raise no dispatcher exceptions");
                    SpatialJson.Save(Path.Combine(output, "results.json"), new { passed = checks.Count, checks, audioPlaybackTested = false });
                }
                app.Dispatcher.BeginInvoke(new Action(async () => { try { await Tests(); } catch (Exception e) { failure = e; } finally { app.Shutdown(); } }));
                app.Run();
            }
            catch (Exception e) { failure = e; }
            finally { model?.StopPlayback(); model?.Cancel(); model?.DetachLanguage(); model?.Visualization.Dispose(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); if (failure is not null) throw failure;
    }
    sealed class CancelAfterSave(CancellationTokenSource stop) : IProgress<ConversionProgress> { public void Report(ConversionProgress value) => stop.Cancel(); }
    static byte[] Pcm(string path) { var wave = AdmWave.Open(path); return File.ReadAllBytes(path).AsSpan((int)wave.DataOffset, (int)wave.DataBytes).ToArray(); }
    static string Scene(string path)
    {
        var doc = XDocument.Parse(Encoding.UTF8.GetString(Chunk(path, "axml")).TrimStart('\uFEFF'));
        foreach (var p in doc.Descendants().Where(e => e.Name.LocalName == "audioProgramme")) p.Attribute("audioProgrammeName")?.Remove();
        return doc.ToString(SaveOptions.DisableFormatting);
    }
    static IEnumerable<(string Id, byte[] Data)> Chunks(string path)
    {
        using var f = File.OpenRead(path); using var r = new BinaryReader(f); f.Position = 12; long data = -1; var sizes = new Dictionary<string, long>();
        while (f.Position + 8 <= f.Length)
        {
            string id = Encoding.ASCII.GetString(r.ReadBytes(4)); uint small = r.ReadUInt32();
            long n = small == uint.MaxValue ? id == "data" ? data : sizes[id] : small;
            byte[] bytes = r.ReadBytes(checked((int)n)); if ((n & 1) != 0) r.ReadByte();
            if (id == "ds64")
            {
                data = (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8));
                for (int i = 0; i < BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)); i++)
                    sizes[Encoding.ASCII.GetString(bytes, 28 + i * 12, 4)] = (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(32 + i * 12));
            }
            yield return (id, bytes);
        }
    }
    static byte[] Chunk(string path, string id) => Chunks(path).Single(c => c.Id == id).Data;
    static int Count(string path, string id) => Chunks(path).Count(c => c.Id == id);
    static void MakeBw64(string source, string destination)
    {
        using var f = new FileStream(destination, FileMode.CreateNew); using var w = new BinaryWriter(f);
        w.Write("BW64"u8); w.Write(uint.MaxValue); w.Write("WAVE"u8); w.Write("ds64"u8); w.Write(40u); w.Write(0ul);
        w.Write((ulong)AdmWave.Open(source).DataBytes); w.Write((ulong)AdmWave.Open(source).Scene.DurationSamples); w.Write(1u);
        w.Write("TEST"u8); w.Write(5ul);
        foreach (var c in Chunks(source)) { w.Write(Encoding.ASCII.GetBytes(c.Id)); w.Write(c.Id == "data" ? uint.MaxValue : (uint)c.Data.Length); w.Write(c.Data); if ((c.Data.Length & 1) != 0) w.Write((byte)0); }
        w.Write("TEST"u8); w.Write(uint.MaxValue); w.Write(new byte[] { 1, 2, 3, 4, 5, 0 });
        long length = f.Length; f.Position = 20; w.Write((ulong)(length - 8));
    }
}
