using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Disc2Flac;

public sealed partial class AudioNavigationService
{
    async Task PlayAdmAsync(PlaylistInfo playlist, long start, long end, CancellationToken token, Action<long>? started,
        Func<double>? volume, Action<long, double>? playbackPosition, Action<long, MixerMeterFrame>? mixedLevels,
        Action<long, double, double[], double[]>? channelLevels, PreviewPcmTap? visualization, PreviewPlaybackControl? transport, Func<PreviewMixState>? outputMix)
    {
        if (ProgressiveAtmos is { } live)
        {
            var (firstLive, count) = AdmTimeline.Range(live.Wave, start, end);
            await live.EnsureAsync(firstLive, Math.Min(live.TotalFrames, firstLive + Math.Min(count, ProgressiveAtmosSource.StartupFrames)), AtmosPresentation, token);
            await PlaySpatialInputAsync(live.Input(firstLive, count, AtmosPresentation), live.Wave, start, token, started, volume,
                playbackPosition, mixedLevels, channelLevels, visualization, transport, outputMix); return;
        }
        var segments = PlaylistSegments.ForRange(playlist, start, end); var file = segments[0].Clip.File!; AudioFileReader.RequireUnchanged(file);
        var wave = file.Adm!;
        if (segments.Any(s => s.Clip.File?.Adm?.Path != wave.Path)) throw new InvalidOperationException("An ADM playback session cannot cross into a different source file.");
        long sourceStart = PlaylistSegments.SourceTicks(segments[0]);
        var (first, frames) = AdmTimeline.Range(wave, sourceStart, sourceStart + end - start);
        await PlaySpatialInputAsync(SpatialInput.FromAdm(wave, first, frames), wave, start, token, started, volume,
            playbackPosition, mixedLevels, channelLevels, visualization, transport, outputMix);
    }
    async Task PlaySpatialInputAsync(SpatialInput input, AdmWave wave, long start, CancellationToken token, Action<long>? started,
        Func<double>? volume, Action<long, double>? playbackPosition, Action<long, MixerMeterFrame>? mixedLevels,
        Action<long, double, double[], double[]>? channelLevels, PreviewPcmTap? visualization, PreviewPlaybackControl? transport, Func<PreviewMixState>? outputMix)
    {
        var session = HeadphoneSession.Current;
        HeadphoneOptions Options() => input.Progressive is not null ? ProgressiveAtmosSource.Options(session.Options, input.Presentation)
            : AdmFileFormat.RenderOptions(ReferenceEquals(SpatialAdmWave, wave) ? session.Options : session.Options with { Enabled = false });
        var settings = Options(); string hash = settings.Method == HeadphoneMethod.MeasuredBrir ? settings.BrirHash : settings.Hrtf == HeadphoneHrtf.Sofa
            ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(settings.SofaPath))) : HrtfEntry.BuiltIn.Hash;
        var snapshot = new SpatialRenderSnapshot(settings, session.Pose, new(StereoMixSettings.Default, null), volume?.Invoke() ?? 1, hash);
        bool own = transport is null; transport ??= new(allowPendingPause: true);
        bool announced = false;
        try
        {
            var stats = await SpatialPcmPlayer.PlayAsync(input, snapshot, transport, token, livePose: true,
                liveOptions: Options, liveMaster: volume, liveOutputMix: outputMix,
                liveSourceMix: () => AdmMonitor.For(wave), observe: frame =>
                {
                    if (!announced && transport.HasStarted) { announced = true; started?.Invoke(start); }
                    playbackPosition?.Invoke(start, transport.EstimatedPosition);
                    double[] peak = [0, 0], power = [0, 0]; int n = frame.Samples.Length / 2;
                    for (int i = 0; i < frame.Samples.Length; i++) { int c = i & 1; double v = frame.Samples[i]; peak[c] = Math.Max(peak[c], Math.Abs(v)); power[c] += v * v; }
                    double Db(double v) => v > 0 ? 20 * Math.Log10(v) : double.NegativeInfinity;
                    var p = peak.Select(Db).ToArray(); var rms = power.Select(v => Db(Math.Sqrt(v / n))).ToArray();
                    mixedLevels?.Invoke(start, new(frame.Seconds, p, rms, [p[0], double.NegativeInfinity], [double.NegativeInfinity, p[1]], p));
                    if (mixedLevels is null) channelLevels?.Invoke(start, frame.Seconds, p, rms);
                    if (visualization?.Enabled() == true)
                    {
                        byte[] bytes = new byte[frame.Samples.Length * 2];
                        for (int i = 0; i < frame.Samples.Length; i++) System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)Math.Clamp((int)Math.Round(frame.Samples[i] * 32768), short.MinValue, short.MaxValue));
                        visualization.TryPublish(new(start / 45000d + frame.Seconds, ["FL", "FR"], bytes, frame.Samples, bytes));
                    }
                });
            log.Write($"ADM OUTPUT frames={stats.Frames} empty-buffer-events={stats.EmptyBuffers} clipped-samples={stats.Clips}");
        }
        finally { if (own) transport.Dispose(); }
    }
}
