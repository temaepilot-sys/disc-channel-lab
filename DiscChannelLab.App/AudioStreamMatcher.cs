using System.Globalization;

namespace Disc2Flac;

public static class AudioStreamMatcher
{
    public static string Identity(AudioStreamInfo stream) =>
        $"{(string.IsNullOrEmpty(stream.TransportId) ? stream.Codec : NormalizeId(stream.TransportId))}|{stream.Language}|{stream.Channels}|{stream.ChannelLayout}";

    private static string NormalizeId(string id)
    {
        var hex = id.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        return long.TryParse(hex ? id[2..] : id, hex ? NumberStyles.HexNumber : NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var value) ? value.ToString(CultureInfo.InvariantCulture) : id;
    }

    public static AudioStreamInfo Resolve(AudioStreamInfo selected, IReadOnlyList<AudioStreamInfo> streams)
    {
        var candidates = streams.Where(x => x.Channels == selected.Channels &&
            (x.Channels <= 2 || string.Equals(x.ChannelLayout, selected.ChannelLayout, StringComparison.OrdinalIgnoreCase)) &&
            (selected.Language.Length == 0 || x.Language.Length == 0 || x.Language == selected.Language)).ToArray();
        if (!string.IsNullOrEmpty(selected.TransportId))
            candidates = candidates.Where(x => NormalizeId(x.TransportId) == NormalizeId(selected.TransportId)).ToArray();
        else
            candidates = candidates.Where(x => x.Codec == selected.Codec && x.Profile == selected.Profile).ToArray();
        // Index is local to a file; it cannot identify a logical audio track across clips.
        return candidates.Length == 1 ? candidates[0] : throw new InvalidDataException(
            candidates.Length == 0 ? "選択した音声に対応するストリームがありません。" : "音声の対応が曖昧なため自動選択できません。");
    }
}
