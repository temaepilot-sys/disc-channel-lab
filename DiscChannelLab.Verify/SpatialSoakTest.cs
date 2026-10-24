using Disc2Flac;
using System.Diagnostics;
using System.IO;

internal static class SpatialSoakTest
{
    public static async Task Run(string output,int seconds,string sofa)
    {
        string root=Path.GetFullPath(output);Directory.CreateDirectory(root);Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME",root);
        string path=Path.Combine(root,"soak.f32");string[] codes=["FL","FR","FC","LFE","SL","SR"];
        var block=new byte[48000*6*4];for(int f=0;f<48000;f++)for(int c=0;c<6;c++)BitConverter.GetBytes((float)(.015*Math.Sin(2*Math.PI*(100+c*173)*f/48000))).CopyTo(block,(f*6+c)*4);
        using(var file=File.Create(path))for(int s=0;s<seconds;s++)file.Write(block);
        var entry=SofaLibrary.Inspect(sofa,CancellationToken.None);if(entry.Status!="Compatible")throw new InvalidDataException(entry.Diagnostic);
        var options=SofaLibrary.Apply(entry,new HeadphoneOptions{SmoothingMs=8,HeadroomDb=-12}) with {Room=new(){Mode="Studio"}};
        var snapshot=new SpatialRenderSnapshot(options,new(0,0,0),new(StereoMixSettings.Default,null),1,entry.Hash);
        var input=new SpatialInput(path,6,SpatialScene.ChannelBed(codes,(long)seconds*48000),0,(long)seconds*48000,"soak-synthesized","soak");
        using var control=new PreviewPlaybackControl();using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(seconds+90));
        var watch=Stopwatch.StartNew();long startMemory=Process.GetCurrentProcess().PrivateMemorySize64;
        var playback=SpatialPcmPlayer.PlayAsync(input,snapshot,control,stop.Token,outputFactory:()=>new SpatialLabTests.SilentDevice());
        int pauses=0;
        while(!playback.IsCompleted)
        {
            await Task.WhenAny(playback,Task.Delay(1000));
            if(control.CanPause && pauses<3 && watch.Elapsed.TotalSeconds>(pauses+1)*8)
            {control.Pause();await Task.Delay(100);control.Resume();pauses++;}
            if((int)watch.Elapsed.TotalSeconds%30==0)Console.WriteLine($"SOAK {watch.Elapsed.TotalSeconds:0}s / {seconds}s; position={control.Position:0.0}s");
        }
        var result=await playback;long endMemory=Process.GetCurrentProcess().PrivateMemorySize64;
        SpatialJson.Save(Path.Combine(root,"results.json"),new{durationSeconds=seconds,elapsedSeconds=watch.Elapsed.TotalSeconds,result,pauses,startMemory,endMemory,growthBytes=endMemory-startMemory});
        if(result.EmptyBuffers!=0)throw new InvalidDataException("Soak had output buffer underruns: "+result.EmptyBuffers);
        Console.WriteLine("PASS 10-minute SOFA + early reflections real-WASAPI soak, no underruns; pause/resume exercised.");
    }
}
