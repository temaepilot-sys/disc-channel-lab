using Disc2Flac;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Xml.Linq;

internal static class SpatialEdgeTests
{
    public static async Task Run(string root,string demo)
    {
        root=Path.GetFullPath(root);Directory.CreateDirectory(root);Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME",root);
        var checks=new List<string>();void Check(bool ok,string label){if(!ok)throw new InvalidDataException(label);checks.Add(label);Console.WriteLine("PASS "+label);}
        var original=AdmWave.Open(demo);var source=File.ReadAllBytes(demo);
        foreach(var magic in new[]{"RF64","BW64"})
        {
            string path=Path.Combine(root,magic+".wav");using(var f=File.Create(path))using(var w=new BinaryWriter(f))
            {w.Write(Encoding.ASCII.GetBytes(magic));w.Write(uint.MaxValue);w.Write(Encoding.ASCII.GetBytes("WAVEds64"));w.Write(28u);w.Write((ulong)(source.Length+36-8));w.Write((ulong)original.DataBytes);w.Write((ulong)original.Scene.DurationSamples);w.Write(0u);var rest=source[12..];BinaryPrimitives.WriteUInt32LittleEndian(rest.AsSpan((int)original.DataOffset-12-4),uint.MaxValue);w.Write(rest);}
            var file=AdmWave.Open(path);Check(file.DataBytes==original.DataBytes&&file.Scene.Sources.Length==3,magic+": ds64 64-bit chunk data and ADM references are retained");
        }
        string nested=Rewrite(root,"nested.wav",source,doc=>
        {
            var content=doc.Descendants().Single(e=>e.Name.LocalName=="audioContent");var target=content.Elements().Last(e=>e.Name.LocalName=="audioObjectIDRef");target.Value="AO_2001";
            var format=doc.Descendants().Single(e=>e.Name.LocalName=="audioFormatExtended");var ns=format.Name.Namespace;
            format.Add(new XElement(ns+"audioObject",new XAttribute("audioObjectID","AO_2001"),new XAttribute("audioObjectName","Parent"),new XAttribute("start","00:00:00.50000"),new XAttribute("duration","00:00:11.00000"),new XElement(ns+"audioObjectIDRef","AO_1002")));
        });
        Check(AdmWave.Open(nested).Scene.Sources.Single(s=>s.Kind==SpatialSourceKind.Object).StartSample==48000,"Nested object start is programme-relative, never parent-relative");
        foreach(var kind in new[]{"diffuse","unknown-track","cycle","bad-jump","missing-block"})
        {
            string path=Rewrite(root,kind+".wav",source,doc=>
            {
                var block=doc.Descendants().First(e=>e.Name.LocalName=="jumpPosition").Parent!;var ns=block.Name.Namespace;
                if(kind=="diffuse")block.Add(new XElement(ns+"diffuse","0.5"));
                if(kind=="bad-jump")block.Elements().First(e=>e.Name.LocalName=="jumpPosition").Value="3";
                if(kind=="unknown-track")doc.Descendants().First(e=>e.Name.LocalName=="audioTrackUIDRef").Value="ATU_DEADBEEF";
                if(kind=="cycle")doc.Descendants().First(e=>e.Name.LocalName=="audioObject").Add(new XElement(ns+"audioObjectIDRef","AO_1001"));
                if(kind=="missing-block")doc.Descendants().Last(e=>e.Name.LocalName=="audioBlockFormat"&&e.Attribute("rtime") is not null).Remove();
            });
            bool rejected=false;try{AdmWave.Open(path);}catch(Exception ex)when(ex is InvalidDataException or NotSupportedException){rejected=true;}
            Check(rejected,"ADM diagnoses "+kind+" rather than silently changing scene content");
        }
        var snapshot=new SpatialRenderSnapshot(new(){Enabled=true,Method=HeadphoneMethod.SteamAudio,HeadroomDb=-12,SmoothingMs=0},new(0,0,0),new(StereoMixSettings.Default,null),1,"builtin-4.8.1");
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var whole=await SpatialRendering.PrepareAdmAsync(original,0,original.Scene.DurationSamples,stop.Token);
        var seek=whole with{FirstSample=2*48000,PcmOffsetFrames=2*48000,Frames=4800};
        var offline=await SpatialRendering.RenderAsync(seek,snapshot,true,stop.Token);var captured=new List<float>();
        using(var control=new PreviewPlaybackControl(startPaused:true))
        {
            var playing=SpatialPcmPlayer.PlayAsync(seek,snapshot,control,stop.Token,outputFactory:()=>new SpatialLabTests.SilentDevice(),rawDspCapture:(samples,n)=>captured.AddRange(samples.Take(n*2)));
            while(!control.DevicePaused&&!playing.IsCompleted){stop.Token.ThrowIfCancellationRequested();await Task.Delay(5);}
            long held=(long)(control.Position*48000);await Task.Delay(100);Check(control.DevicePaused&&control.Position*48000==held,"Scene seek can start paused without sounding a source block");
            control.Resume();await playing;
        }
        var expected=System.Runtime.InteropServices.MemoryMarshal.Cast<byte,float>(File.ReadAllBytes(offline.PcmPath)).ToArray();
        Check(captured.SequenceEqual(expected),"Seek PCM offset, fixed pose, partial final block and native tail equal offline output");
        var rotated=await SpatialRendering.RenderAsync(seek,snapshot with{Pose=new(90,0,0)},true,stop.Token);
        Check(!File.ReadAllBytes(offline.PcmPath).SequenceEqual(File.ReadAllBytes(rotated.PcmPath)),"Changing head direction renders world-fixed ADM sources differently");
        var mute=seek with{Scene=seek.Scene with{Sources=seek.Scene.Sources.Select(s=>s with{Blocks=s.Blocks.Select(b=>b with{Gain=0}).ToArray()}).ToArray()}};
        var silence=await SpatialRendering.RenderAsync(mute,snapshot,false,stop.Token);
        var metric=await SpatialRendering.MeasureAsync(silence,new ToolPaths().Ffmpeg,stop.Token);
        Check(silence.Peak==0&&metric.IntegratedLufs is null,"Silence is not reported as a valid loudness match");
        SpatialJson.Save(Path.Combine(root,"results.json"),new{passed=checks.Count,checks});
    }
    static string Rewrite(string root,string name,byte[] source,Action<XDocument> edit)
    {
        string path=Path.Combine(root,name);using var input=new BinaryReader(new MemoryStream(source));using var stream=File.Create(path);using var output=new BinaryWriter(stream);
        output.Write(input.ReadBytes(12));
        while(input.BaseStream.Position+8<=input.BaseStream.Length)
        {
            var id=input.ReadBytes(4);int length=checked((int)input.ReadUInt32());var data=input.ReadBytes(length);if((length&1)!=0)input.ReadByte();
            if(Encoding.ASCII.GetString(id)=="axml"){var doc=XDocument.Parse(Encoding.UTF8.GetString(data));edit(doc);data=Encoding.UTF8.GetBytes(doc.ToString());}
            output.Write(id);output.Write((uint)data.Length);output.Write(data);if((data.Length&1)!=0)output.Write((byte)0);
        }
        long total=stream.Length;stream.Position=4;output.Write((uint)(total-8));return path;
    }
}
