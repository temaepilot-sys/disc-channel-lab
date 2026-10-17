using System.Numerics;

namespace Disc2Flac;

/// <summary>Reads user-selected SOFA data without passing Unicode file names to native fopen.</summary>
public static class SofaHrtf
{
    public const int MaximumFileBytes = 128 * 1024 * 1024;
    internal static byte[] Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException(LanguageService.T("SOFAファイルを選択してください。"));
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 8 or > MaximumFileBytes)
            throw new InvalidDataException(LanguageService.T("SOFAファイルは8バイト以上、128MiB以下である必要があります。"));
        var data = new byte[(int)file.Length];
        file.ReadExactly(data);
        ReadOnlySpan<byte> signature = [0x89, 0x48, 0x44, 0x46, 0x0d, 0x0a, 0x1a, 0x0a];
        if (!data.AsSpan(0, 8).SequenceEqual(signature))
            throw new InvalidDataException(LanguageService.T("対応するHDF5形式のSOFAファイルではありません。"));
        return data;
    }

    public static void Validate(string path)
    {
        var options = new HeadphoneOptions { Method = HeadphoneMethod.SteamAudio, Hrtf = HeadphoneHrtf.Sofa, SofaPath = path };
        using var renderer = new SteamHeadphoneSpatializer(1, options);
        var input = new float[HeadphoneRenderer.BlockSize]; input[0] = .01f;
        var left = new float[input.Length]; var right = new float[input.Length];
        bool response = false;
        // Validate actual filters, including oblique directions, without opening an output device.
        foreach (var direction in new[] { Vector3.UnitZ, -Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitX,
            Vector3.Normalize(new Vector3(1, 0, 1)), Vector3.Normalize(new Vector3(-1, 0, -1)),
            Vector3.Normalize(new Vector3(0, 1, 1)), Vector3.Normalize(new Vector3(0, -1, 1)) })
        {
            renderer.Process(0, input, left, right, direction, options);
            foreach (var sample in left.Concat(right))
            {
                if (!float.IsFinite(sample)) throw new InvalidDataException(LanguageService.T("SOFAの音響フィルターに無効な値が含まれています。"));
                response |= Math.Abs(sample) > 1e-12f;
            }
        }
        if (!response) throw new InvalidDataException(LanguageService.T("SOFAの音響フィルターから応答を取得できませんでした。"));
    }
}
