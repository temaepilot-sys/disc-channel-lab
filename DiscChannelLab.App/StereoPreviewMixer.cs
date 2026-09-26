using System.Buffers.Binary;
using System.Diagnostics;

namespace Disc2Flac;

public sealed record PreviewMixState(StereoMixSettings Mix, string? SoloChannel);

/// <summary>Applies preview channel gains to decoded PCM while playback is running.</summary>
public static class StereoPreviewMixer
{
    public static async Task<long> CopyAsync(Stream source, Stream destination, AudioStreamInfo stream,
        Func<PreviewMixState> settings, Func<double> volume, CancellationToken token,
        Action? firstWrite = null, Action<PreviewMixState>? mixApplied = null)
    {
        var channels = StereoMixSettings.ChannelNames(stream);
        if (channels.Count != stream.Channels)
            throw new InvalidOperationException("このチャンネル配置は試聴できません。");

        const int sampleRate = 48000;
        const int framesPerBuffer = 1024;
        const double maxQueuedSeconds = 0.06;
        var frameBytes = channels.Count * sizeof(short);
        var input = new byte[framesPerBuffer * frameBytes + frameBytes];
        var output = new byte[framesPerBuffer * 2 * sizeof(short)];
        var left = new double[channels.Count];
        var right = new double[channels.Count];
        var targetLeft = new double[channels.Count];
        var targetRight = new double[channels.Count];
        var clock = Stopwatch.StartNew();
        var carried = 0;
        var submittedFrames = 0L;
        var rampFrames = 0;
        var gain = SafeGain(volume());
        var targetGain = gain;
        var gainStep = 0d;
        PreviewMixState? active = null;
        var started = false;

        int count;
        while ((count = await source.ReadAsync(input.AsMemory(carried, framesPerBuffer * frameBytes - carried), token)
                   .ConfigureAwait(false)) > 0)
        {
            var available = count + carried;
            var frames = available / frameBytes;
            if (frames == 0) { carried = available; continue; }
            var complete = frames * frameBytes;
            var wait = (submittedFrames + frames) / (double)sampleRate - maxQueuedSeconds - clock.Elapsed.TotalSeconds;
            if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), token).ConfigureAwait(false);

            var requested = settings();
            if (requested != active)
            {
                BuildWeights(channels, requested, targetLeft, targetRight);
                active = requested;
                rampFrames = submittedFrames == 0 ? 1 : 480;
                mixApplied?.Invoke(requested);
            }
            var requestedGain = SafeGain(volume());
            if (requestedGain != targetGain)
            {
                targetGain = requestedGain;
                gainStep = (targetGain - gain) / 480;
            }

            for (var frame = 0; frame < frames; frame++)
            {
                if (rampFrames > 0)
                {
                    for (var channel = 0; channel < channels.Count; channel++)
                    {
                        left[channel] += (targetLeft[channel] - left[channel]) / rampFrames;
                        right[channel] += (targetRight[channel] - right[channel]) / rampFrames;
                    }
                    rampFrames--;
                }
                if (gain != targetGain)
                {
                    gain += gainStep;
                    if (Math.Abs(gain - targetGain) <= Math.Abs(gainStep)) gain = targetGain;
                }
                var leftSample = 0d;
                var rightSample = 0d;
                var offset = frame * frameBytes;
                for (var channel = 0; channel < channels.Count; channel++)
                {
                    var sample = BinaryPrimitives.ReadInt16LittleEndian(input.AsSpan(offset + channel * 2, 2));
                    leftSample += sample * left[channel];
                    rightSample += sample * right[channel];
                }
                BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(frame * 4, 2), Clip(leftSample * gain));
                BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(frame * 4 + 2, 2), Clip(rightSample * gain));
            }
            await destination.WriteAsync(output.AsMemory(0, frames * 4), token).ConfigureAwait(false);
            if (!started) { started = true; firstWrite?.Invoke(); }
            submittedFrames += frames;
            carried = available - complete;
            if (carried != 0) input.AsSpan(complete, carried).CopyTo(input);
        }
        if (carried != 0) throw new InvalidDataException("再生音声のサンプルが途中で切れています。");
        return submittedFrames * frameBytes;
    }

    public static void BuildWeights(IReadOnlyList<string> channels, PreviewMixState state,
        double[] left, double[] right)
    {
        Array.Clear(left);
        Array.Clear(right);
        if (state.SoloChannel is { } solo)
        {
            var index = -1;
            for (var i = 0; i < channels.Count; i++)
                if (channels[i] == solo) { index = i; break; }
            if (index < 0) throw new InvalidOperationException("選択したチャンネルは音源にありません。");
            left[index] = right[index] = 1;
            return;
        }

        var surroundCount = channels.Count(x => x is "BL" or "SL" or "TFL");
        var rightSurroundCount = channels.Count(x => x is "BR" or "SR" or "TFR");
        for (var i = 0; i < channels.Count; i++)
        {
            switch (channels[i])
            {
                case "FL": left[i] = state.Mix.Front; break;
                case "FR": right[i] = state.Mix.Front; break;
                case "FC": left[i] = right[i] = state.Mix.Center; break;
                case "BL" or "SL" or "TFL": left[i] = state.Mix.Surround / surroundCount; break;
                case "BR" or "SR" or "TFR": right[i] = state.Mix.Surround / rightSurroundCount; break;
                case "BC": left[i] = right[i] = state.Mix.Surround / 2; break;
                case "LFE": left[i] = right[i] = state.Mix.Lfe; break;
                case "FLC": left[i] = 0.5 * state.Mix.Front; break;
                case "FRC": right[i] = 0.5 * state.Mix.Front; break;
            }
        }
        Normalize(left);
        Normalize(right);
    }

    private static void Normalize(double[] weights)
    {
        var total = weights.Sum();
        if (total > 0)
            for (var i = 0; i < weights.Length; i++) weights[i] /= total;
    }

    private static short Clip(double value) =>
        (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue);

    private static double SafeGain(double requested) =>
        double.IsFinite(requested) ? Math.Clamp(requested, 0, 2) : 1;
}
