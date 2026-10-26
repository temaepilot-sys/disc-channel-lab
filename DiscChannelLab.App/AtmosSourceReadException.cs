namespace Disc2Flac;

/// <summary>A demuxer read failure is distinct from an object decoder or authored-timeline error.</summary>
public sealed class AtmosSourceReadException(string clip, string diagnostic) : IOException(
    LanguageService.T("音声データを最後まで読み取れませんでした。ディスク、ISOのマウント状態、接続を確認してから再実行してください。") +
    "\nClip: " + clip + (string.IsNullOrWhiteSpace(diagnostic) ? "" : "\n" + diagnostic[^Math.Min(diagnostic.Length, 1800)..]));
