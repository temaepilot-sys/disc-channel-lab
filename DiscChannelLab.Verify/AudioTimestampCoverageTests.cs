using Disc2Flac;
using System.Diagnostics;
using System.IO;

internal static class AudioTimestampCoverageTests
{
    public static async Task Run(string output, string? source)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidDataException(message); checks.Add(message); Console.WriteLine("PASS " + message); }
        void Reject(string message, params string[] packets)
        {
            bool rejected = false;
            try { var p = new AudioTimestampCoverage.PacketTimeline("1/90000"); foreach (string line in packets) p.Add(line); p.Complete(); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, message);
        }
        const string first = "pts=54000000|duration=75|flags=K__|side_data_type=MPEGTS Stream ID|id=253";
        var good = new AudioTimestampCoverage.PacketTimeline("1/90000");
        good.Add(""); good.Add(first); good.Add("side_data_type=MPEGTS Stream ID|id=253");
        good.Add("pts=54000075|duration=75|flags=___");
        Check(good.Complete() == new AudioTimestampCoverage(28800000, 28800080, 2),
            "Audio packet timestamps retain the absolute 600-second source origin and exact 40-sample access units");
        var rational = new AudioTimestampCoverage.PacketTimeline("2/180000");
        Check(rational.Sample(54000075) == 28800040, "Timestamp conversion honors both parts of the rational time base");
        var large = new AudioTimestampCoverage.PacketTimeline("1/90000");
        for (int i = 0; i < 212400; i++) large.Add($"pts={54000000 + i * 75L}|duration=75|flags=___");
        Check(large.Complete() == new AudioTimestampCoverage(28800000, 37296000, 212400),
            "A full-size clip is accumulated incrementally without retaining packet history");
        Reject("An empty selected stream cannot prove silence");
        Reject("Missing packet PTS is rejected", "pts=N/A|duration=75|flags=___");
        Reject("Missing packet duration is rejected", "pts=54000000|duration=N/A|flags=___");
        Reject("Corrupt audio packets are rejected", "pts=54000000|duration=75|flags=__C");
        Reject("Discarded audio packets are rejected", "pts=54000000|duration=75|flags=_D_");
        Reject("Zero duration cannot establish an audio endpoint", "pts=54000000|duration=0|flags=___");
        Reject("Interior timestamp gaps cannot be concealed as silence", first, "pts=54000150|duration=75|flags=___");
        Reject("Timestamp overlaps are rejected", first, "pts=54000000|duration=75|flags=___");
        bool badBase = false;
        try { _ = new AudioTimestampCoverage.PacketTimeline("1/0"); } catch (InvalidDataException) { badBase = true; }
        Check(badBase, "An invalid time base is rejected");

        object? actual = null;
        if (source is not null)
        {
            var tools = new ToolPaths(); var log = new AppLog(Path.Combine(output, "logs"));
            var watch = Stopwatch.StartNew();
            var a = await AudioTimestampCoverage.ReadAsync(tools.Ffprobe, source, 1, log, CancellationToken.None);
            var b = await AudioTimestampCoverage.ReadAsync(tools.Ffprobe, source, 3, log, CancellationToken.None);
            Check(a.PacketCount == 212400 && a.Start == 28800000 && a.End == 37296000,
                "E-drive failing clip: all 212400 packets verify the 8496000 decoded samples despite missing duration_ts");
            Check(b.PacketCount > 0 && b.Start == a.Start && b.End == a.End,
                "The other TrueHD track is selected explicitly and verified independently");
            using var stop = new CancellationTokenSource(); stop.Cancel(); bool canceled = false;
            try { await AudioTimestampCoverage.ReadAsync(tools.Ffprobe, source, 1, log, stop.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled, "Canceled verification does not report success");
            actual = new { Source = Path.GetFileName(source), First = a, Second = b, Seconds = watch.Elapsed.TotalSeconds };
        }
        SpatialJson.Save(Path.Combine(output, "results.json"), new { passed = checks.Count, checks, actual });
    }
}
