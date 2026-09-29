using System.Buffers.Binary;
using System.Diagnostics;

namespace Disc2Flac;

public sealed record PreviewMixState(StereoMixSettings Mix, string? SoloChannel, ExperimentalChannel[]? Channels = null);

/// <summary>Applies preview channel gains to decoded PCM while playback is running.</summary>
public static class StereoPreviewMixer
{
    public static ExperimentalChannel[] StandardChannels(AudioStreamInfo stream)
    {
        var channels = StereoMixSettings.ChannelNames(stream);
        var left = new double[channels.Count]; var right = new double[channels.Count];
        BuildWeights(channels, new(StereoMixSettings.Default, null), left, right);
        return channels.Select((code, i) =>
        {
            // Convert the standard L/R coefficients to gain plus equal-power pan.
            // The attenuation is visible in the faders; 100% remains true unity.
            var gain = Math.Sqrt(left[i] * left[i] + right[i] * right[i]);
            var pan = gain == 0 ? 0 : Math.Atan2(right[i], left[i]) * 4 / Math.PI - 1;
            return new ExperimentalChannel(code, gain, Math.Clamp(pan, -1, 1), false);
        }).ToArray();
    }

    public static async Task<long> CopyAsync(Stream source, Stream destination, AudioStreamInfo stream,
        Func<PreviewMixState> settings, Func<double> volume, CancellationToken token,
        Action? firstWrite = null, Action<PreviewMixState>? mixApplied = null, Action<MixerMeterFrame>? mixedLevels = null)
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
        var clock = new Stopwatch();
        var carried = 0;
        var submittedFrames = 0L;
        var rampFrames = 0;
        var gain = SafeGain(volume());
        var targetGain = gain;
        var gainStep = 0d;
        PreviewMixState? active = null;
        var started = false;
        var meterInput = new double[channels.Count];
        var meterInputSquares = new double[channels.Count];
        var meterLeft = new double[channels.Count];
        var meterRight = new double[channels.Count];
        var meterOutput = new double[2];
        var meterFrames = 0;
        var pendingMeters = new List<MixerMeterFrame>();

        int count;
        while ((count = await source.ReadAsync(input.AsMemory(carried, framesPerBuffer * frameBytes - carried), token)
                   .ConfigureAwait(false)) > 0)
        {
            var available = count + carried;
            var frames = available / frameBytes;
            if (frames == 0) { carried = available; continue; }
            var complete = frames * frameBytes;
            // Decoder startup/seek time must not become a burst of queued audio.
            if (!clock.IsRunning) clock.Start();
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
                    var contributionLeft = sample * left[channel];
                    var contributionRight = sample * right[channel];
                    leftSample += contributionLeft;
                    rightSample += contributionRight;
                    if (mixedLevels is not null)
                    {
                        var normalized = sample / 32768d;
                        meterInput[channel] = Math.Max(meterInput[channel], Math.Abs(normalized));
                        meterInputSquares[channel] += normalized * normalized;
                        meterLeft[channel] = Math.Max(meterLeft[channel], Math.Abs(contributionLeft) / 32768);
                        meterRight[channel] = Math.Max(meterRight[channel], Math.Abs(contributionRight) / 32768);
                    }
                }
                if (mixedLevels is not null)
                {
                    meterOutput[0] = Math.Max(meterOutput[0], Math.Abs(leftSample * gain) / 32768);
                    meterOutput[1] = Math.Max(meterOutput[1], Math.Abs(rightSample * gain) / 32768);
                    if (++meterFrames == 960)
                    {
                        pendingMeters.Add(FinishMeterWindow(submittedFrames + frame + 1));
                    }
                }
                BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(frame * 4, 2), Clip(leftSample * gain));
                BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(frame * 4 + 2, 2), Clip(rightSample * gain));
            }
            await destination.WriteAsync(output.AsMemory(0, frames * 4), token).ConfigureAwait(false);
            if (!started) { started = true; firstWrite?.Invoke(); }
            submittedFrames += frames;
            // Never announce peaks for PCM that has not yet been submitted to the player.
            foreach (var meter in pendingMeters) mixedLevels?.Invoke(meter);
            pendingMeters.Clear();
            carried = available - complete;
            if (carried != 0) input.AsSpan(complete, carried).CopyTo(input);
        }
        if (carried != 0) throw new InvalidDataException("再生音声のサンプルが途中で切れています。");
        if (mixedLevels is not null && meterFrames > 0) mixedLevels(FinishMeterWindow(submittedFrames));
        return submittedFrames * frameBytes;

        MixerMeterFrame FinishMeterWindow(long endFrame)
        {
            var result = new MixerMeterFrame((endFrame - meterFrames / 2d) / sampleRate,
                meterInput.Select(ToDb).ToArray(),
                meterInputSquares.Select(sum => ToDb(Math.Sqrt(sum / meterFrames))).ToArray(),
                meterLeft.Select(ToDb).ToArray(), meterRight.Select(ToDb).ToArray(), meterOutput.Select(ToDb).ToArray());
            Array.Clear(meterInput); Array.Clear(meterInputSquares);
            Array.Clear(meterLeft); Array.Clear(meterRight); Array.Clear(meterOutput);
            meterFrames = 0;
            return result;
        }
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

        if (state.Channels is { } experimental)
        {
            var hasSolo = experimental.Any(x => x.Solo && channels.Contains(x.Code));
            // Independent faders are true channel gains: 100% is unity before pan.
            // Do not apply the group downmix's shared normalization or layout weights.
            for (var i = 0; i < channels.Count; i++)
            {
                var setting = experimental.FirstOrDefault(x => x.Code == channels[i]);
                if (setting is null) { left[i] = right[i] = 0; continue; }
                var gain = setting.Muted || (hasSolo && !setting.Solo) || !double.IsFinite(setting.Gain)
                    ? 0 : Math.Clamp(setting.Gain, 0, 1);
                if (setting.InvertPolarity) gain = -gain;
                var pan = double.IsFinite(setting.Pan) ? Math.Clamp(setting.Pan, -1, 1) : 0;
                var angle = (pan + 1) * Math.PI / 4;
                left[i] = pan == 1 ? 0 : gain * Math.Cos(angle);
                right[i] = pan == -1 ? 0 : gain * Math.Sin(angle);
            }
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
        Scale(left, StereoMixSettings.ReferenceTotal(channels, left: true));
        Scale(right, StereoMixSettings.ReferenceTotal(channels, left: false));
    }

    private static void Scale(double[] weights, double divisor)
    {
        for (var i = 0; i < weights.Length; i++) weights[i] /= divisor;
    }

    private static double ToDb(double amplitude) => amplitude > 0 ? 20 * Math.Log10(amplitude) : double.NegativeInfinity;

    private static short Clip(double value) =>
        (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue);

    private static double SafeGain(double requested) =>
        double.IsFinite(requested) ? Math.Clamp(requested, 0, 2) : 1;
}
