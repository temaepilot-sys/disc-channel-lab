using Disc2Flac;
using System.Buffers.Binary;
using System.IO;
using System.Text;

internal static class DamfPlaylistTests
{
    public static async Task Run(string output)
    {
        string root = Path.GetFullPath(output); Directory.CreateDirectory(root);
        List<string> checks = [];
        void Check(bool ok, string text) { if (!ok) throw new InvalidDataException(text); checks.Add(text); Console.WriteLine("PASS " + text); }
        const int firstFrames = 8217, secondFrames = 389;
        string first = Fixture("first", firstFrames, false), second = Fixture("second", secondFrames, true);
        var album = new AdmAlbumMetadata("Original segment test", "Synthetic author", null, [new(0, "First"), new(firstFrames, "Second")]);
        var parts = new[] { new DamfSegment(first, firstFrames), new DamfSegment(second, secondFrames) };
        string target = Path.Combine(root, "combined.wav");
        await DamfImporter.WritePlaylistAsync(parts, target, album, null, CancellationToken.None);
        var adm = AdmWave.Open(target);
        Check(adm.Channels == 3 && adm.Scene.DurationSamples == firstFrames + secondFrames,
            "Combined ADM has the union of source slots and the complete exact sample duration");
        Check(adm.Album?.Chapters.Length == 2 && adm.Album.Chapters[1].StartSample == firstFrames,
            "Chapter boundaries retain the original sample coordinate");
        using (var file = File.OpenRead(target))
        {
            file.Position = adm.DataOffset; byte[] bytes = new byte[adm.DataBytes]; file.ReadExactly(bytes);
            bool exact = true;
            for (int f = 0; f < firstFrames + secondFrames; f++) for (int c = 0; c < 3; c++)
            {
                int at = (f * 3 + c) * 3; int actual = bytes[at] | bytes[at + 1] << 8 | bytes[at + 2] << 16; actual = actual << 8 >> 8;
                int expected = f < firstFrames ? c == 2 ? 0 : (c + 1) * 1000 + f
                    : -((c == 0 ? 2 : c == 1 ? 1 : 3) * 1000 + f - firstFrames);
                if (actual != expected) exact = false;
            }
            Check(exact, "Every PCM sample is exact across channel reordering, 24-bit endian conversion, missing slots and partial final buffers");
        }
        var moving = adm.Scene.Sources.Single(s => s.TrackIndex == 1);
        Check(moving.At(firstFrames - 1).Position.X > .99 && moving.At(firstFrames).Position.X < -.49,
            "Position jumps at the clip boundary instead of interpolating across independent decoder states");
        Check(Math.Abs(moving.At(firstFrames).Gain - Math.Pow(10, -6d / 20)) < 1e-6,
            "Gain at the second clip is reset to its own initial metadata");
        var added = adm.Scene.Sources.Single(s => s.TrackIndex == 2);
        Check(added.At(firstFrames - 1).Gain == 0 && added.At(firstFrames).Gain == 1,
            "A source introduced by a later clip is silent before that clip");
        string padded = Path.Combine(root, "verified-source-gap.wav");
        await DamfImporter.WritePlaylistAsync([new(first, firstFrames + 200, LeadingSilenceFrames: 80, TrailingSilenceFrames: 120)], padded, album, null, CancellationToken.None);
        var gapWave = AdmWave.Open(padded); byte[] gapBytes = new byte[gapWave.DataBytes];
        using (var file = File.OpenRead(padded)) { file.Position = gapWave.DataOffset; file.ReadExactly(gapBytes); }
        var gapObject = gapWave.Scene.Sources.Single(s => s.TrackIndex == 1);
        Check(gapBytes.Take(80 * 6).All(x => x == 0) && gapBytes.TakeLast(120 * 6).All(x => x == 0) &&
            gapObject.At(79).Gain == 0 && gapObject.At(80).Gain == 1 && gapObject.At(firstFrames + 80).Gain == 0,
            "Explicitly verified empty source intervals contain silence and correctly offset active object metadata");
        Check(gapBytes.AsSpan(80 * 6, firstFrames * 6).SequenceEqual(File.ReadAllBytes(Path.Combine(root, "first.audio")).AsSpan(68)),
            "The actual native clip PCM remains byte-exact between the verified empty timestamp regions");
        bool protectedTarget = false;
        try { await DamfImporter.WritePlaylistAsync(parts, target, album, null, CancellationToken.None); }
        catch (IOException) { protectedTarget = true; }
        Check(protectedTarget && AdmWave.Open(target).DataBytes == adm.DataBytes, "Existing completed ADM is preserved");
        bool mismatch = false;
        try { await DamfImporter.WritePlaylistAsync([new(first, firstFrames + 1)], Path.Combine(root, "invalid.wav"), album, null, CancellationToken.None); }
        catch (InvalidDataException) { mismatch = true; }
        Check(mismatch && !File.Exists(Path.Combine(root, "invalid.wav")), "Duration mismatches are rejected without padding or dropping samples");
        string changed = Path.Combine(root, "role-change.atmos");
        File.WriteAllText(changed, File.ReadAllText(second).Replace("channel: LFE", "channel: C"));
        bool role = false;
        try { await DamfImporter.WritePlaylistAsync([parts[0], new(changed, secondFrames)], Path.Combine(root, "wrong-role.wav"), album, null, CancellationToken.None); }
        catch (NotSupportedException) { role = true; }
        Check(role && !File.Exists(Path.Combine(root, "wrong-role.wav")), "Reused IDs with incompatible speaker/object roles cannot silently misroute audio");
        using var cancel = new CancellationTokenSource(); string canceled = Path.Combine(root, "canceled.wav");
        bool canceledCorrectly = false;
        try { await DamfImporter.WritePlaylistAsync(parts, canceled, album, new Immediate(_ => cancel.Cancel()), cancel.Token); }
        catch (OperationCanceledException) { canceledCorrectly = true; }
        Check(canceledCorrectly && !File.Exists(canceled) && !Directory.GetFiles(root, "*.partial*").Any(),
            "Cancellation removes only incomplete generated files and leaves no partial ADM or XML");
        Check(File.Exists(first) && File.Exists(second) && File.Exists(target), "Cancellation preserves input clips and the earlier completed export");
        SpatialJson.Save(Path.Combine(root, "results.json"), new { passed = checks.Count, checks });

        string Fixture(string name, int frames, bool swapped)
        {
            string audio = name + ".audio", metadata = name + ".metadata", path = Path.Combine(root, name + ".atmos");
            // The second CAF changes byte order and object track order, and introduces Object 20.
            File.WriteAllText(path, $"version: 0.5.1\npresentations:\n  - type: home\n    audio: {audio}\n    metadata: {metadata}\n    offset: 0.0\n    creationTool: truehdd\n    bedInstances:\n      - channels:\n          - channel: LFE\n            ID: 3\n    objects:\n" +
                (swapped ? "      - ID: 20\n      - ID: 10\n" : "      - ID: 10\n"));
            File.WriteAllText(Path.Combine(root, metadata), "sampleRate: 48000\nevents:\n  - ID: 10\n    samplePos: 0\n    pos: " +
                (swapped ? "[-0.5,0,0]\n    gain: -6\n    rampLength: 20\n  - ID: 20\n    samplePos: 0\n    pos: [0,1,0]\n" : "[-1,0,0]\n  - ID: 10\n    samplePos: 8190\n    pos: [1,0,0]\n    rampLength: 10\n"));
            int channels = swapped ? 3 : 2; using var file = File.Create(Path.Combine(root, audio));
            void Big32(int value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, value); file.Write(b); }
            void Big64(long value) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, value); file.Write(b); }
            file.Write("caff\0\u0001\0\0desc"u8); Big64(32); Big64(BitConverter.DoubleToInt64Bits(48000)); file.Write("lpcm"u8);
            Big32(swapped ? 0 : 2); Big32(channels * 3); Big32(1); Big32(channels); Big32(24);
            file.Write("data"u8); Big64((long)frames * channels * 3 + 4); Big32(0);
            for (int f = 0; f < frames; f++) for (int c = 0; c < channels; c++)
            {
                // Second file is [LFE, Object 20, Object 10]; expected global order is [LFE, Object 10, Object 20].
                int value = swapped ? -((c == 0 ? 2 : c == 1 ? 3 : 1) * 1000 + f) : (c + 1) * 1000 + f;
                if (swapped) { file.WriteByte((byte)(value >> 16)); file.WriteByte((byte)(value >> 8)); file.WriteByte((byte)value); }
                else { file.WriteByte((byte)value); file.WriteByte((byte)(value >> 8)); file.WriteByte((byte)(value >> 16)); }
            }
            return path;
        }
    }
    sealed class Immediate(Action<ConversionProgress> report) : IProgress<ConversionProgress> { public void Report(ConversionProgress value) => report(value); }
}
