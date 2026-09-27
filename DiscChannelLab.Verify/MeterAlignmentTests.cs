using Disc2Flac;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;

internal static class MeterAlignmentTests
{
    public static async Task RunAsync()
    {
        var audio = new AudioStreamInfo
        { Index = 0, Codec = "pcm_s16le", SampleRate = 48000, Channels = 6, ChannelLayout = "5.1", BitDepth = 16 };
        const int sampleCount = 960 * 5 + 137;
        var pcm = new byte[sampleCount * 12];
        // Impulses straddle meter, PCM buffer and fragmented-read boundaries.
        foreach (var (index, value) in new[] { (959, -32768), (960, 16384), (1023, 8192), (1920, 4096), (3840, 2048), (4936, 1024) })
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(index * 12, 2), (short)value);
        using var source = new FragmentedStream(pcm);
        using var output = new MemoryStream();
        var frames = new List<MixerMeterFrame>();
        var started = false;
        await StereoPreviewMixer.CopyAsync(source, output, audio,
            () => new PreviewMixState(StereoMixSettings.Default, "FL"), () => 1, CancellationToken.None,
            firstWrite: () => started = true, mixedLevels: frame =>
            {
                Check(started, "Meter announced before first PCM submission");
                var end = Math.Min((frames.Count + 1) * 960, sampleCount);
                Check(output.Length >= end * 4, "Meter announced unsubmitted samples");
                frames.Add(frame);
            });
        Check(frames.Count == 6, "Partial final window was lost");
        for (var i = 0; i < frames.Count; i++)
        {
            var start = i * 960; var end = Math.Min(start + 960, sampleCount);
            var peak = 0d; var squares = 0d;
            for (var sample = start; sample < end; sample++)
            {
                var input = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(sample * 12, 2)) / 32768d;
                peak = Math.Max(peak, Math.Abs(input)); squares += input * input;
            }
            var expected = Db(peak);
            var frame = frames[i];
            Check(Math.Abs(frame.Seconds - (start + end) / 96000d) < 1e-10, "Wrong sample timestamp");
            Check(Equal(frame.Input[0], expected) && Equal(frame.Left[0], expected) &&
                  Equal(frame.Right[0], expected) && Equal(frame.Output[0], expected), "Input/Post/Output window mismatch");
            Check(Equal(frame.InputRms[0], Db(Math.Sqrt(squares / (end - start)))), "Input RMS window mismatch");
            Check(frame.Input.Skip(1).All(double.IsNegativeInfinity), "Input leaked to another channel");
        }
        Console.WriteLine("Impulse peaks/RMS match across Input, Post and Output; fragmented reads and final partial window verified.");

        var model = new MainViewModel();
        try
        {
            model.SelectedStream = audio;
            Check(ReferenceEquals(model.ChannelMeters[0], model.MixerStrips[0].Input), "Standard GUI uses a different input meter");
            var queue = typeof(MainViewModel).GetMethod("QueueMixedFrame", BindingFlags.Instance | BindingFlags.NonPublic)!;
            queue.Invoke(model, [.1d, frames[0]]);
            Check(model.ChannelMeters[0].PeakPercent == 0 && model.MixerStrips[0].PostLeft.PeakPercent == 0,
                "Meters displayed ahead of playback");
            typeof(MainViewModel).GetField("_previewSeconds", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, .1d);
            model.AdvancePlaybackClock();
            Check(model.ChannelMeters[0].PeakPercent == 100 && model.MixerStrips[0].PostLeft.PeakPercent == 100 &&
                  model.MixerOutputLeft.PeakPercent == 100, "Meters did not update together at playback position");

            // PreviewSeconds is the dragged position; playback is still near zero.
            typeof(MainViewModel).GetField("_isPlaying", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, true);
            typeof(MainViewModel).GetField("_previewSeconds", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, 100d);
            var clock = (Stopwatch)typeof(MainViewModel).GetField("_playClock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
            model.IsScrubbing = true; clock.Start();
            queue.Invoke(model, [10d, frames[3]]);
            Check(model.ChannelMeters[0].PeakPercent == 100 && model.MixerOutputLeft.PeakPercent == 100,
                "Dragged slider moved meters ahead of the sound");
            clock.Stop();
        }
        finally { model.Cancel(); model.DetachLanguage(); }
        Console.WriteLine("Standard GUI and mixer share Input values; one playback clock schedules all meters, including during seek dragging.");
    }
    private static double Db(double x) => x > 0 ? 20 * Math.Log10(x) : double.NegativeInfinity;
    private static bool Equal(double a, double b) => a == b || Math.Abs(a - b) < 1e-9;
    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private sealed class FragmentedStream(byte[] pcm) : MemoryStream(pcm)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 137)], token);
    }
}
