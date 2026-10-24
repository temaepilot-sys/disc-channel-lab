using Disc2Flac;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

internal static class DistributionTests
{
    public static void Run(string personal, string publicBuild, string output)
    {
        personal = Path.GetFullPath(personal); publicBuild = Path.GetFullPath(publicBuild);
        List<string> checks = [];
        void Check(bool condition, string name) { if (!condition) throw new InvalidDataException(name); checks.Add(name); Console.WriteLine("PASS " + name); }
        (Dictionary<int, string> Methods, string[] Resources, string Name) Read(string folder)
        {
            using var stream = File.OpenRead(Path.Combine(folder, "DiscChannelLab.dll")); using var pe = new PEReader(stream); var metadata = pe.GetMetadataReader();
            var methods = new Dictionary<int, string>();
            foreach (var handle in metadata.MethodDefinitions)
            {
                var method = metadata.GetMethodDefinition(handle); if (method.RelativeVirtualAddress == 0) continue;
                var body = pe.GetMethodBody(method.RelativeVirtualAddress);
                methods[MetadataTokens.GetToken(handle)] = metadata.GetString(method.Name) + ":" + Convert.ToHexString(SHA256.HashData(body.GetILBytes()!));
            }
            return (methods, metadata.ManifestResources.Select(h => metadata.GetString(metadata.GetManifestResource(h).Name)).ToArray(), metadata.GetString(metadata.GetAssemblyDefinition().Name));
        }
        var a = Read(personal); var b = Read(publicBuild);
        Check(a.Name == "DiscChannelLab" && b.Name == "DiscChannelLab", "Both distribution modes use the formal DiscChannelLab identity");
        Check(a.Methods.Count == b.Methods.Count && a.Methods.All(p => b.Methods.TryGetValue(p.Key, out var body) && p.Value == body), "All managed method IL, including playback and DSP, is identical in personal and public builds");
        Check(a.Resources.Count(s => s.StartsWith("Disc2Flac.tools.")) == 1 && !b.Resources.Any(s => s.StartsWith("Disc2Flac.tools.")), "Only the personal build embeds the private FFmpeg tool bundle");
        foreach (string folder in new[] { personal, publicBuild })
        {
            Check(File.Exists(Path.Combine(folder, "PureHDF.dll")) && File.Exists(Path.Combine(folder, "third_party/PureHDF/LICENSE")) && File.Exists(Path.Combine(folder, "third_party/libadm-common-definitions/LICENSE")), "Retained PureHDF / ADM runtime resources and notices: " + Path.GetFileName(folder));
            Check(File.Exists(Path.Combine(folder, "SpaceSketch/DiscChannelLab.Visualizer.exe")) && File.Exists(Path.Combine(folder, "Guides/comparison.md")) && File.Exists(Path.Combine(folder, "Guides/comparison.ja.md")), "Both viewer and English/Japanese Spatial Lab guides are included: " + Path.GetFileName(folder));
        }
        string[] prohibited = ["ffmpeg.exe", "ffprobe.exe", "ffplay.exe", "truehdd.exe", "sacd_extract.exe", "prepared-adm-library.json"];
        string[] media = [".sofa", ".thd", ".caf", ".m2ts", ".flac", ".wav", ".f32", ".pdb"];
        Check(!Directory.GetFiles(publicBuild, "*", SearchOption.AllDirectories).Any(p => prohibited.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase) || media.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)), "Public output contains no codec executable, personal audio, response dataset or debug symbols");
        Check(!Directory.Exists(Path.Combine(publicBuild, "Presets/BRIR")) && File.Exists(Path.Combine(personal, "Presets/BRIR/D1-48k-5.1.brir.json")), "User-specific BRIR presets remain personal resources only");
        SpatialJson.Save(Path.GetFullPath(output), new { passed = checks.Count, checks, comparedManagedMethods = a.Methods.Count, personalDllSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(personal, "DiscChannelLab.dll")))), publicDllSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(publicBuild, "DiscChannelLab.dll")))) });
    }
}
