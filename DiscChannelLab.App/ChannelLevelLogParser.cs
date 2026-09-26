using System.Globalization;

namespace Disc2Flac;

// Reads the short, per-frame metadata emitted by the FFmpeg analysis branch.
internal sealed class ChannelLevelLogParser(int channelCount, long segmentStartTicks,
    Action<long, double, double[], double[]> onFrame)
{
    private readonly double[] _peaks = new double[channelCount];
    private readonly double[] _rms = new double[channelCount];
    private double _seconds;
    private bool _hasFrame;
    private bool _hasLevel;

    public bool Consume(string line)
    {
        var frame = line.IndexOf("frame:", StringComparison.Ordinal);
        if (frame >= 0 && line.Contains("pts_time:", StringComparison.Ordinal))
        {
            Flush();
            var start = line.IndexOf("pts_time:", frame, StringComparison.Ordinal) + "pts_time:".Length;
            var end = start;
            while (end < line.Length && !char.IsWhiteSpace(line[end])) end++;
            _hasFrame = start >= "pts_time:".Length &&
                double.TryParse(line.AsSpan(start, end - start), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out _seconds);
            Array.Fill(_peaks, double.NegativeInfinity);
            Array.Fill(_rms, double.NegativeInfinity);
            _hasLevel = false;
            return true;
        }

        var marker = line.IndexOf("lavfi.astats.", StringComparison.Ordinal);
        if (marker < 0) return false;
        var channelStart = marker + "lavfi.astats.".Length;
        var separator = line.IndexOf('.', channelStart);
        if (!_hasFrame || separator < 0 || !int.TryParse(line.AsSpan(channelStart, separator - channelStart),
                NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
            number < 1 || number > channelCount) return true;
        var equals = line.IndexOf('=', separator + 1);
        if (equals < 0) return true;
        var value = line.AsSpan(equals + 1).Trim();
        var db = value.Equals("-inf".AsSpan(), StringComparison.OrdinalIgnoreCase)
            ? double.NegativeInfinity
            : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed : double.NegativeInfinity;
        if (line.AsSpan(separator + 1, equals - separator - 1).SequenceEqual("Peak_level"))
            _peaks[number - 1] = db;
        else if (line.AsSpan(separator + 1, equals - separator - 1).SequenceEqual("RMS_level"))
            _rms[number - 1] = db;
        _hasLevel = true;
        return true;
    }

    public void Flush()
    {
        if (!_hasFrame || !_hasLevel) return;
        onFrame(segmentStartTicks, _seconds, (double[])_peaks.Clone(), (double[])_rms.Clone());
        _hasFrame = false;
    }
}
