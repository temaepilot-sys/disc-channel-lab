using Disc2Flac;
using System.IO;
using System.Security.Cryptography;

internal static class AdmExportReadbackTests
{
    public static async Task Run(string file, string output)
    {
        string root = Path.GetFullPath(output); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", root);
        var wave = AdmWave.Open(Path.GetFullPath(file)); List<string> checks = []; List<object> renders = [];
        void Check(bool ok, string text) { if (!ok) throw new InvalidDataException(text); checks.Add(text); Console.WriteLine("PASS " + text); }
        Check(wave.DataBytes == wave.Scene.DurationSamples * wave.Channels * 3 && wave.Scene.Sources.Select(s => s.TrackIndex).Distinct().Count() == wave.Channels,
            "Reopened ADM maps every stored PCM track and its complete declared byte range");
        Check(wave.Album?.Chapters.Length > 0 && wave.Album.Chapters.All(c => c.StartSample >= 0 && c.StartSample < wave.Scene.DurationSamples),
            "Every retained chapter lies inside the completed audio timeline");
        string hash;
        await using (var input = File.OpenRead(file)) hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input));
        Check(hash.Length == 64, "The complete generated WAV was read through to EOF and SHA-256 hashed");
        var options = new HeadphoneOptions { Enabled = true, Method = HeadphoneMethod.SteamAudio, HeadroomDb = -18,
            SmoothingMs = 0, Room = SpatialRoomOptions.Preset("Big Room") };
        var snapshot = new SpatialRenderSnapshot(options, new(0, 0, 0), new(StereoMixSettings.Default, null), 1, HrtfEntry.BuiltIn.Hash);
        double maximumPeak = 0;
        foreach (long first in new[] { 0, wave.Scene.DurationSamples / 2, Math.Max(0, wave.Scene.DurationSamples - 48000) })
        {
            var input = SpatialInput.FromAdm(wave, first, Math.Min(48000, wave.Scene.DurationSamples - first));
            var rendered = await SpatialRendering.RenderAsync(input, snapshot, true, CancellationToken.None);
            Check(rendered.SourceFrames == input.Frames && double.IsFinite(rendered.Peak),
                $"Completed ADM renders real position-aware audio at {first / 48000d:0.000}s, including Big Room");
            maximumPeak = Math.Max(maximumPeak, rendered.Peak);
            renders.Add(new { first, rendered.SourceFrames, rendered.TailFrames, rendered.Peak, rendered.ClippedSamples });
        }
        Check(maximumPeak > 0, "The rendered inspection intervals include nonzero decoded audio");
        SpatialJson.Save(Path.Combine(root, "results.json"), new { passed = checks.Count, checks, FileSha256 = hash, wave.Channels,
            wave.Scene.DurationSamples, Chapters = wave.Album!.Chapters.Length, renders, humanListening = false });
    }
}
