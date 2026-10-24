using Disc2Flac;
using PureHDF;
using PureHDF.Selections;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

internal static class BrirD1Tests
{
    public static async Task Run(string output,string directory,string manifest,string pcmFile)
    {
        string root=Path.GetFullPath(output);Directory.CreateDirectory(root);Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME",root);
        var checks=new List<string>();var results=new List<object>();
        void Check(bool ok,string name){if(!ok)throw new InvalidDataException(name);checks.Add(name);Console.WriteLine("PASS "+name);}
        var baseProfile=BrirLibrary.ReadProfile(manifest);PreparedBrir? selected=null;
        foreach(string file in Directory.GetFiles(directory,"*.sofa").Order())
        {
            using var h=H5File.OpenRead(file);var shape=h.Dataset("Data.IR").Space.Dimensions;
            int rate=(int)h.Dataset("Data.SamplingRate").Read<double[]>()[0];
            Check(h.Attribute("SOFAConventions").Read<string>()=="SingleRoomSRIR"&&shape[0]==50&&shape[1]==2,"D1: measured-room FIR, 50 directions and two ears at "+rate+" Hz");
            var view=h.Dataset("ListenerView").Read<double[]>();var up=h.Dataset("ListenerUp").Read<double[]>();var ears=h.Dataset("ReceiverPosition").Read<double[]>();var listener=h.Dataset("ListenerPosition").Read<double[]>();
            Check(view.SequenceEqual(new[]{1d,0,0})&&up.SequenceEqual(new[]{0d,0,1})&&ears.SequenceEqual(new[]{0d,.09,0,0,-.09,0})&&listener.All(v=>v==0),"D1: fixed forward head, left +Y / right -Y receivers and stationary listener verified at "+rate+" Hz");
            var positions=h.Dataset("SourcePosition").Read<double[]>();
            foreach(var (index,angle) in new[]{(2,0d),(9,45d),(15,90d),(21,135d),(33,225d),(39,270d),(45,315d)})
                if(positions[index*3]!=angle||positions[index*3+1]!=0||positions[index*3+2]!=1.5)throw new InvalidDataException("Unexpected D1 source positions.");
            Check(true,"D1: explicit horizontal M indices and 1.5 m measured radius verified at "+rate+" Hz");
            string profilePath=Path.Combine(root,"D1-"+rate+".brir.json");SpatialJson.Save(profilePath,baseProfile with{Routes=baseProfile.Routes.Select(r=>r with{File=Path.GetFullPath(file)}).ToArray()});
            var prepared=await BrirLibrary.PrepareAsync(profilePath);prepared.RequireChannels(["FL","FR","FC","LFE","SL","SR","BL","BR"]);
            Check(prepared.Responses.Count==7&&prepared.ResamplingLatencyFrames==(rate==48000?0:192),"D1: real SOFA loads all seven measured routes with explicit dry LFE at "+rate+" Hz");
            Check(prepared.Responses.Values.All(x=>x.Left.Any(v=>v!=0)&&x.Right.Any(v=>v!=0)&&x.Left.All(float.IsFinite)&&x.Right.All(float.IsFinite)),"D1: finite non-silent measured responses for both ears at "+rate+" Hz");
            if(rate==48000)
            {
                var d=h.Dataset("Data.IR");int n=(int)shape[2];
                var raw=d.Read<double[]>(fileSelection:new HyperslabSelection(3,[9,0,0],[1,2,shape[2]]));
                Check(prepared.Responses["FL"].Left.AsSpan().SequenceEqual(raw.Take(n).Select(x=>(float)x).ToArray())&&prepared.Responses["FL"].Right.AsSpan().SequenceEqual(raw.Skip(n).Select(x=>(float)x).ToArray()),"D1: left/right IR samples exactly match M=9 with no resampling, trimming or normalization");
                selected=prepared;
            }
            string after;using(var stream=File.OpenRead(file))after=Convert.ToHexStringLower(SHA256.HashData(stream));
            Check(prepared.Assets.All(a=>a.Sha256==after),"D1: original measurement file unchanged at "+rate+" Hz");
            results.Add(new{file,rate,shape,prepared.Hash,prepared.ResamplingLatencyFrames});
        }
        if(selected is null)throw new InvalidDataException("48 kHz D1 response missing.");
        string[] channels=["FL","FR","FC","LFE","SL","SR","BL","BR"];
        var input=new SpatialInput(Path.GetFullPath(pcmFile),8,SpatialScene.ChannelBed(channels,288000),0,288000,"Synthetic 7.1 source through real SADIE II D1 BRIR","test");
        var options=new HeadphoneOptions{Enabled=true,Method=HeadphoneMethod.MeasuredBrir,BrirPath=selected.Path,BrirHash=selected.Hash,HeadroomDb=-18};
        var snapshot=new SpatialRenderSnapshot(options,new(0,0,0),new(StereoMixSettings.Default,null),1,selected.Hash);
        var rendered=await SpatialRendering.RenderAsync(input,snapshot,true,CancellationToken.None);
        Check(rendered.Peak>0&&rendered.ClippedSamples==0&&rendered.Snapshot.Brir?.Assets.Length==7,"D1: real 7.1 measured rendering is audible, finite and records exact measurement hashes");
        using var captured=new MemoryStream();using var transport=new PreviewPlaybackControl();
        var stats=await SpatialPcmPlayer.PlayAsync(input,snapshot,transport,CancellationToken.None,outputFactory:()=>new SpatialLabTests.SilentDevice(),rawDspCapture:(samples,n)=>captured.Write(MemoryMarshal.AsBytes(samples.AsSpan(0,n*2))));
        Check(captured.ToArray().AsSpan().SequenceEqual(File.ReadAllBytes(rendered.PcmPath)),"D1: actual-device pre-output PCM matches offline measured BRIR PCM including room tail");
        Check(stats.EmptyBuffers==0,"D1: 7.1 real WASAPI scheduling without empty buffers (test output silent)");
        SpatialJson.Save(Path.Combine(root,"results.json"),new{passed=checks.Count,checks,results,stats,humanListening=false});
    }
}
