using Disc2Flac;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;

internal static class GaplessPlaybackTests
{
    public static void Run(string folder, string? discSource = null)
    {
        var root = Path.GetFullPath(folder); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", Path.Combine(root, "profile"));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try { Pump(Test()); } catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();

        async Task Test()
        {
            var checks = new List<string>();
            void Check(bool ok, string text)
            { if (!ok) throw new InvalidDataException(text); checks.Add(text); Console.WriteLine("PASS " + text); }
            var log = new AppLog(Path.Combine(root, "logs")); var tools = new ToolPaths(); var runner = new ProcessRunner(log);
            var file = Path.Combine(root, "continuous.wav");
            await runner.RunAsync(tools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "aevalsrc=0.08|0.08:s=48000:c=stereo",
                "-t", "2.1", "-c:a", "pcm_s24le", "-y", file], CancellationToken.None);
            var model = new MainViewModel(new TrackEditsStore(log, Path.Combine(root, "metadata")));
            var originalFactory = AudioNavigationService.HeadphoneOutputFactory; var originalOptions = HeadphoneSession.Current.Options;
            var outputs = new List<RecordingOutput>(); var titles = new List<int>(); int stopped = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => model.AdvancePlaybackClock();
            PropertyChangedEventHandler record = (_, e) =>
            {
                if (e.PropertyName == nameof(model.SelectedTrackTitle) && model.IsPlaying) titles.Add(model.SelectedTrack!.Number);
                if (e.PropertyName == nameof(model.IsPlaying) && !model.IsPlaying) stopped++;
            };
            try
            {
                await model.OpenAudioFilesAsync([file]); model.PreviewSeconds = .7; model.SplitAtPreview();
                model.PreviewSeconds = .7; model.SplitAtPreview(); model.SelectedTrack = model.Tracks[0];
                Check(model.Tracks.Count == 3, "A continuous WAV is split into three real UI tracks");
                model.PropertyChanged += record;
                AudioNavigationService.HeadphoneOutputFactory = () => { var output = new RecordingOutput(); outputs.Add(output); return output; };
                timer.Start();
                foreach (var mode in new[] { PreviewAudioMode.Stereo, PreviewAudioMode.HeadphonesHrtf })
                {
                    model.SelectedPreviewMode = mode; model.SelectedTrack = model.Tracks[0]; model.ContinuousPlayback = true;
                    outputs.Clear(); titles.Clear(); stopped = 0;
                    var epochField = typeof(MainViewModel).GetField("_transportVersion", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    await model.TogglePlaybackAsync(); int epoch = (int)epochField.GetValue(model)!;
                    await Until(() => outputs.Count == 1 && outputs[0].Disposed && !model.IsPlaying);
                    var sink = outputs.Single();
                    Check(titles.SequenceEqual(new[] { 2, 3 }) && stopped == 1 && (int)epochField.GetValue(model)! == epoch,
                        mode + ": track labels change in order without stopping playback or resetting the visualizer epoch");
                    Check(sink.EmptyBufferCount == 0, mode + ": one Windows audio device remains active with no empty buffers");
                    var samples = sink.Samples.ToArray(); int first = Array.FindIndex(samples, s => Math.Abs(s) > 1e-7f);
                    int prefix = first - 1; // The single transport attack starts with one zero sample.
                    foreach (var boundary in new[] { 33600, 67200 })
                    {
                        var window = samples.Skip(prefix + boundary - 256).Take(512).ToArray();
                        Check(window.Length == 512 && window.All(s => Math.Abs(s) > .0001) &&
                            (mode != PreviewAudioMode.Stereo || window.All(s => Math.Abs(s - .08) < 2e-6)),
                            mode + $": source remains continuous at sample {boundary}; no inserted silence, fade or gain dip");
                    }
                    Check(model.SelectedTrack == model.Tracks[2] && Math.Abs(model.PreviewSeconds - .7) < .001,
                        mode + ": final track displays its own elapsed time");
                }
                model.SelectedPreviewMode = PreviewAudioMode.Stereo;
                var album = Path.Combine(root, "Separate files"); Directory.CreateDirectory(album);
                foreach (var item in new[] { ("01.flac", "flac", "48000"), ("02.m4a", "alac", "44100") })
                    await runner.RunAsync(tools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", $"aevalsrc=0.08|0.08:s={item.Item3}:c=stereo",
                        "-t", "0.7", "-c:a", item.Item2, "-y", Path.Combine(album, item.Item1)], CancellationToken.None);
                await model.OpenSourceAsync(album); outputs.Clear(); titles.Clear(); stopped = 0;
                await model.TogglePlaybackAsync(); await Until(() => !model.IsPlaying && outputs.Count > 0 && outputs.All(o => o.Disposed));
                Check(outputs.Count == 1 && outputs[0].EmptyBufferCount == 0,
                    "Separate FLAC/48kHz and ALAC/44.1kHz files share one Windows output with no empty buffer");
                var separate = outputs[0].Samples.ToArray(); int origin = Array.FindIndex(separate, s => Math.Abs(s) > 1e-7f) - 1;
                Check(separate.Skip(origin + 33600 - 128).Take(256).All(s => Math.Abs(s - .08) < 2e-6),
                    "The file/codec/rate boundary inserts no silence or second transport fade");
                Check(model.SelectedStream is { Codec: "alac", SampleRate: 44100 } && model.SelectedTrack == model.Tracks[1],
                    "Automatic file transitions update source format and track metadata without rebuilding the mixer");
                await model.OpenAudioFilesAsync([file]);
                model.SelectedTrack = model.Tracks[0]; outputs.Clear(); titles.Clear(); stopped = 0;
                await model.TogglePlaybackAsync();
                await Until(() => model.SelectedTrack == model.Tracks[1] && model.PreviewSeconds > .08);
                model.PausePlayback(); await Until(() => outputs[0].Paused);
                var held = outputs[0]; var count = held.PlayedFrames; await Task.Delay(80);
                Check(held.PlayedFrames == count && model.PreviewSeconds < .4, "Pause after an automatic transition freezes the device and keeps track-relative time");
                await model.TogglePlaybackAsync(); await Until(() => !held.Paused && held.PlayedFrames > count);
                Check(outputs.Count == 1, "Resume in the second track retains the shared output and decoder");
                await model.SeekAsync(.1); await Until(() => outputs.Count == 2 && outputs[1].HasStarted);
                Check(model.SelectedTrack == model.Tracks[1] && model.PreviewSeconds < .4, "Seek after a seamless transition uses the current track's start");
                model.StopPlayback(); await Until(() => outputs.All(o => o.Disposed));

                model.SelectedTrack = model.Tracks[0]; outputs.Clear(); model.ContinuousPlayback = true;
                await model.TogglePlaybackAsync(); await Until(() => model.PreviewSeconds > .15);
                model.ContinuousPlayback = false;
                await Until(() => !model.IsPlaying && outputs[0].Disposed);
                Check(model.SelectedTrack == model.Tracks[0] && outputs.Count == 1 && model.PreviewSeconds == model.PreviewMax,
                    "Turning Continuous play off finishes the current track, even with later audio already buffered");
                var single = outputs[0];
                int leading = single.Samples.FindIndex(s => Math.Abs(s) > 1e-7f) - 1;
                Check(single.SubmittedFrames - leading * 2 - HeadphonePlaybackOutput.FadeFrames == 33600,
                    "A dynamic stop boundary retains the exact 33,600 source samples without playing the next track");

                model.SelectedTrack = model.Tracks[0]; outputs.Clear(); model.ContinuousPlayback = false;
                await model.TogglePlaybackAsync(); await Until(() => !model.IsPlaying && outputs[0].Disposed);
                Check(outputs.Count == 1 && model.SelectedTrack == model.Tracks[0], "Continuous play off at startup still plays one track only");

                if (discSource is not null)
                {
                    await model.OpenSourceAsync(discSource); model.StopInspection(); await model.InspectionTask;
                    var video = model.Playlists.First(p => p.Format == DiscFormat.DvdVideo && p.DvdVideoTitle == 1);
                    model.SelectedPlaylist = video; await model.ChoosePlaylistAsync(video); model.StopInspection();
                    model.SelectedTrack = model.Tracks[1]; model.PreviewSeconds = model.PreviewMax - .4;
                    model.ContinuousPlayback = true; outputs.Clear(); titles.Clear(); stopped = 0;
                    await model.TogglePlaybackAsync();
                    await Until(() => model.SelectedTrack == model.Tracks[2] && model.PreviewSeconds > .2);
                    Check(outputs.Count == 1 && stopped == 0 && outputs[0].EmptyBufferCount == 0,
                        "Actual DVD-Video crosses chapter 2 to 3 with one Windows output and no buffer gap");
                    model.StopPlayback(); await Until(() => outputs.All(o => o.Disposed));
                }
                File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { checks }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally
            {
                timer.Stop(); model.PropertyChanged -= record; model.Cancel(); model.DetachLanguage(); model.Visualization.Dispose();
                AudioNavigationService.HeadphoneOutputFactory = originalFactory; HeadphoneSession.Current.SetOptions(originalOptions);
            }
        }
    }
    static async Task Until(Func<bool> test)
    { var clock = Stopwatch.StartNew(); while (!test()) { if (clock.Elapsed.TotalSeconds > 20) throw new TimeoutException("Seamless playback check timed out."); await Task.Delay(5); } }
    static void Pump(Task task)
    { if (!task.IsCompleted) { var frame = new DispatcherFrame(); _ = task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext()); Dispatcher.PushFrame(frame); } task.GetAwaiter().GetResult(); }
    sealed class RecordingOutput : IHeadphoneOutput
    {
        readonly WasapiHeadphoneOutput output = new(); readonly float[] silence = new float[512];
        public readonly List<float> Samples = [];
        public volatile bool Disposed, Paused;
        public bool SupportsPause => true;
        public void Pause() { output.Pause(); Paused = true; }
        public void Resume() { output.Resume(); Paused = false; }
        public long SubmittedFrames => output.SubmittedFrames;
        public long PlayedFrames => output.PlayedFrames;
        public double BufferMilliseconds => output.BufferMilliseconds;
        public double StreamLatencyMilliseconds => output.StreamLatencyMilliseconds;
        public bool HasStarted => output.HasStarted;
        public long EmptyBufferCount => output.EmptyBufferCount;
        public void WaitForSpace(int frames, CancellationToken token) => output.WaitForSpace(frames, token);
        public void Write(float[] pcm, int frames)
        { for (int f = 0; f < frames; f++) Samples.Add(pcm[f * 2]); output.Write(silence, frames); }
        public void Drain(CancellationToken token) => output.Drain(token);
        public void Dispose() { output.Dispose(); Disposed = true; }
    }
}
