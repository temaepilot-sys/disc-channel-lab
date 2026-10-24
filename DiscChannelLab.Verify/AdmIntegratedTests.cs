using Disc2Flac;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class AdmIntegratedTests
{
 public static void Run(string output,string small,string full,bool albumOnly=false,bool driveOnly=false)
 {
  string root=Path.GetFullPath(output);Directory.CreateDirectory(root);Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME",root);
  Exception? error=null;var thread=new Thread(()=>{
   MainWindow? window=null;var oldFactory=AudioNavigationService.HeadphoneOutputFactory;
   try{
    var app=new App();app.InitializeComponent();WpfTestApplication.SuppressStartupWindow(app);ThemeService.SetDark(true);SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
    var model=new MainViewModel();window=new MainWindow(model){ShowActivated=false};
    var loaded=typeof(MainWindow).GetMethod("Window_Loaded",BindingFlags.NonPublic|BindingFlags.Instance)!;
    window.Loaded-=(RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler),window,loaded);window.Show();
    var errors=new List<string>();app.DispatcherUnhandledException+=(_,e)=>{errors.Add(e.Exception.ToString());e.Handled=true;};
    var checks=new List<string>();var details=new List<object>();
    void Check(bool ok,string text){if(!ok)throw new InvalidDataException(text);checks.Add(text);Console.WriteLine("PASS "+text);}
    async Task Tests(){
     if(driveOnly){
      var offers=new List<SpatialInputOffer>();model.ChooseSpatialInput=o=>{offers.Add(o);return Task.FromResult(SpatialOpenChoice.Standard);};await model.OpenSourceAsync("E:\\");
      Check(model.CurrentAdm is null && model.CanPlay && model.Streams.Any(AtmosImportService.IsAtmos) && !model.HeadphonesEnabled,"Opening E drive detects Atmos and No retains its original channel presentation");
      Check(model.Tracks.Count==23&&offers.Count==0&&model.IsAdmAvailable&&!model.IsAdmPlaybackEnabled,"Drive detection enables the ADM choice without a prompt or activation and preserves all 23 chapters");
      var disc=(DiscAnalysis)typeof(MainViewModel).GetField("_disc",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(model)!;
      var playlist=(PlaylistInfo)typeof(MainViewModel).GetField("_loadedPlaylist",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(model)!;
      var originalTitles=model.Tracks.Select(t=>t.Title).ToArray();
      var enableWatch=Stopwatch.StartNew();await model.EnableAdmPlaybackAsync();double enableSeconds=enableWatch.Elapsed.TotalSeconds;
      Check(model.IsAtmosStreamingEnabled&&model.CurrentAdm is{Channels:16}&&model.CanPlay&&model.HeadphonesEnabled,"Explicit drive enable starts live positional HRTF without requiring an ADM archive");
      Check(model.Tracks.Select(t=>t.Title).SequenceEqual(originalTitles)&&offers.Count==0,"Explicit live enable keeps every original chapter title without prompts");
      var live=model.CurrentAdm!.Progressive!;var expected=AdmWave.Open(full);
      async Task<byte[]> ReadRange(SpatialInput input){using var pcm=SpatialPcmSource.Open(input);using var memory=new MemoryStream();await pcm.CopyToAsync(memory);return SHA256.HashData(memory.ToArray());}
      foreach(long first in new[]{0L,120L*48000}){
       var actual=await ReadRange(live.Input(first,48000,AtmosPresentation.Objects));var reference=await ReadRange(SpatialInput.FromAdm(expected,first,48000));
       Check(actual.SequenceEqual(reference),$"Physical E-drive progressive PCM at {first/48000}s equals its complete ADM sample-for-sample");
       double error=0;for(long f=first;f<first+48000;f+=997)for(int c=0;c<expected.Channels;c++){var a=live.Scene.Sources[c].At(f);var b=expected.Scene.Sources[c].At(f);error=Math.Max(error,Math.Max((a.Position-b.Position).Length(),Math.Abs(a.Gain-b.Gain)));}
       Check(error<1e-6,$"Physical E-drive object position and gain at {first/48000}s equal its complete ADM");
      }
      Check(live.PreparedFrames<140L*48000&&!live.Complete,"Physical E-drive seek prepares only the requested prefix, without rendering the 94-minute album first");
      details.Add(new{enableSeconds,preparedSeconds=live.PreparedFrames/48000d,totalSeconds=live.TotalFrames/48000d,cacheBytes=Directory.GetFiles(live.CacheFolder).Sum(p=>new FileInfo(p).Length),fullAlbumRealTimeListening=false});
      Save(window,Path.Combine(root,"main-drive-adm.png"));
     }else if(albumOnly){
      model.ChooseSpatialInput=o=>Task.FromResult(SpatialOpenChoice.Spatial);await model.OpenAudioFilesAsync([full]);await model.EnableAdmPlaybackAsync();
      Check(model.Tracks.Count==23 && model.CanPlay && model.CurrentAdm is{Channels:16},"Full-disc ADM opens through ordinary Open audio file with 23 tracks and 16 sources");
      var sequence=new List<int>();model.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(model.SelectedTrackTitle)&&model.IsPlaying&&model.SelectedTrack is{}t)sequence.Add(t.Number);};
      var sinks=new List<FastOutput>();AudioNavigationService.HeadphoneOutputFactory=()=>{var sink=new FastOutput();sinks.Add(sink);return sink;};
      model.ContinuousPlayback=true;sequence.Add(model.SelectedTrack!.Number);var watch=Stopwatch.StartNew();await model.TogglePlaybackAsync();
      while(model.IsPlaying){model.AdvancePlaybackClock();await Task.Delay(10);if(watch.Elapsed.TotalMinutes>8)throw new TimeoutException("Full ADM playback");}
      model.AdvancePlaybackClock();
      Check(sequence.Distinct().SequenceEqual(Enumerable.Range(1,23)),"One Play visits every chapter in order on the playback clock");
      Check(sinks.Count==1 && sinks[0].Disposed && sinks[0].Finite && sinks[0].Peak>.001,"Entire 94-minute source renders through one continuous device/DSP session without nonfinite samples");
      long expectedMedia=(model.CurrentAdm!.Scene.DurationSamples+255)/256*256+1024;
      Check(sinks[0].SubmittedFrames==expectedMedia+2*HeadphonePlaybackOutput.MinimumGuardFrames,$"All full-album frames delivered plus exact filter tail and endpoint guards ({sinks[0].SubmittedFrames} frames)");
      details.Add(new{elapsed=watch.Elapsed.TotalSeconds,sinks=sinks.Select(s=>new{s.SubmittedFrames,s.Peak,s.Finite,s.Disposed}),sequence,kind="accelerated output sink; not 94 minutes of real-time hardware listening"});
      model.SelectedTrack=model.Tracks[22];await model.PreviousTrackAsync();Check(model.SelectedTrack.Number==22,"Previous track works at album end");await model.NextTrackAsync();Check(model.SelectedTrack.Number==23,"Next track restores final chapter");
      Save(window,Path.Combine(root,"main-full-album.png"));
      var lab=new SpatialLabWindow(model){Owner=window,ShowActivated=false};lab.Show();((TabControl)lab.FindName("Pages")).SelectedItem=lab.FindName("AdmSceneTab");Pump(120);
      Check(((DataGrid)lab.FindName("ObjectsGrid")).Items.Count==16,"Source scene automatically follows the ADM opened by the main player");Save(lab,Path.Combine(root,"scene-full-album.png"));lab.Close();
     }else{
      int prompts=0;model.ChooseSpatialInput=o=>{prompts++;return Task.FromResult(SpatialOpenChoice.Spatial);};
      await model.OpenAudioFilesAsync([small]);Check(prompts==0&&model.CurrentAdm is{Channels:16}&&!model.IsAdmPlaybackEnabled&&!model.HeadphonesEnabled&&model.CanChooseSpatialSource&&model.CanPlay,"Opening ADM enables the explicit source button without a prompt or HRTF activation");await model.EnableAdmPlaybackAsync();
      Check(model.SelectedStream!.Codec=="adm"&&model.SelectedStream.DisplayName.Contains("16 PCM"),"Main format display distinguishes object PCM from rendered stereo output");
      var adm=model.CurrentAdm!;var direct=SpatialInput.FromAdm(adm,48000,48000);var cached=await SpatialRendering.PrepareAdmAsync(adm,48000,48000,CancellationToken.None);
      var snapshot=new SpatialRenderSnapshot(new(){Enabled=true,Method=HeadphoneMethod.SteamAudio},new(0,0,0),new(StereoMixSettings.Default,null),1,HrtfEntry.BuiltIn.Hash);
      var a=await SpatialRendering.RenderAsync(direct,snapshot,true,CancellationToken.None);var b=await SpatialRendering.RenderAsync(cached,snapshot,true,CancellationToken.None);
      Check(File.ReadAllBytes(a.PcmPath).SequenceEqual(File.ReadAllBytes(b.PcmPath)),"Streaming ADM range reader exactly matches materialized float PCM and HRTF output");
      Check(direct.PcmPath==adm.Path&&direct.Wave==adm,"Streaming playback does not create another full-size float audio cache");
      model.ChooseSpatialInput=_=>Task.FromResult(SpatialOpenChoice.Standard);await model.OpenAudioFilesAsync([small]);Check(!model.HeadphonesEnabled&&model.CanPlay&&!model.IsAdmPlaybackEnabled,"Opening ADM again defaults to standard playback without activating ADM");
      await model.EnableAdmPlaybackAsync();await model.DisableAdmPlaybackAsync();Check(model.CanPlay&&model.Tracks.Count!=0&&!model.CanOpenAdmScene&&!model.CanExportAdm,"Disabling ADM preserves the source and disables its scene and export controls");
      model.ChooseSpatialInput=_=>Task.FromResult(SpatialOpenChoice.Spatial);await model.OpenAudioFilesAsync([small]);await model.EnableAdmPlaybackAsync();
      var sinks=new List<FastOutput>();AudioNavigationService.HeadphoneOutputFactory=()=>{var s=new FastOutput();sinks.Add(s);return s;};model.ContinuousPlayback=false;
      await model.TogglePlaybackAsync();await Until(()=>!model.IsPlaying,model,TimeSpan.FromSeconds(15));Check(sinks.Count==1&&sinks[0].Peak>.001&&sinks[0].Finite,"Main Play renders real Atmos-derived object audio, not a decoder guess of 16ch layout");
      string copied=Path.Combine(root,"copied-full-title.wav");Check(await model.ExportAdmAsync(copied)&&SHA256.HashData(File.ReadAllBytes(copied)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(small))),"Save ADM preserves PCM, positions and chapters byte-for-byte");
      model.OutputFolder=Path.Combine(root,"flac-cd");Check(await model.ConvertAsync(),"Main FLAC button renders ADM using selected CD quality");
      var tools=new ToolPaths();var probe=new FfprobeService(tools,new ProcessRunner(new AppLog(Path.Combine(root,"logs"))));string cd=Directory.GetFiles(model.OutputFolder,"*.flac",SearchOption.AllDirectories).Single();var cdData=await probe.ProbeAudioFileAsync(cd,CancellationToken.None);
      Check(cdData.Analysis.Streams[0] is{SampleRate:44100,BitDepth:16,Channels:2},"ADM CD export is 16-bit / 44.1 kHz stereo with original track tags");
      model.IsHighResSelected=true;model.OutputFolder=Path.Combine(root,"flac-48");Check(await model.ConvertAsync(),"ADM also exports 24-bit / 48 kHz shared-renderer FLAC");
      var pcmData=await probe.ProbeAudioFileAsync(Directory.GetFiles(model.OutputFolder,"*.flac",SearchOption.AllDirectories).Single(),CancellationToken.None);Check(pcmData.Analysis.Streams[0] is{SampleRate:48000,BitDepth:24,Channels:2},"24-bit / 48 kHz quality setting matches actual output");
      model.ChooseSpatialInput=_=>throw new InvalidOperationException("Normal FLAC must not prompt for ADM");await model.OpenAudioFilesAsync([cd]);Check(model.CurrentAdm is null&&model.CanPlay&&model.SelectedStream.Codec=="flac","Ordinary audio remains on its original path without an ADM prompt");
      string mixed=Path.Combine(root,"Mixed source folder");Directory.CreateDirectory(mixed);File.Copy(small,Path.Combine(mixed,"01-objects.wav"));
      await new ProcessRunner(new AppLog(Path.Combine(root,"logs"))).RunAsync(tools.Ffmpeg,["-hide_banner","-v","error","-i",cd,"-c","copy","-metadata","track=","-metadata","tracknumber=",Path.Combine(mixed,"02-stereo.flac")],CancellationToken.None);
      int mixedPrompts=0;model.ChooseSpatialInput=_=>{mixedPrompts++;return Task.FromResult(SpatialOpenChoice.Spatial);};await model.OpenSourceAsync(mixed);
      Check(model.Tracks.Count==2&&mixedPrompts==0&&model.IsAdmAvailable&&!model.IsAdmPlaybackEnabled,"Mixed ADM and normal folders enable the ADM choice without a prompt");await model.EnableAdmPlaybackAsync();
      sinks.Clear();await model.TogglePlaybackAsync();await Until(()=>!model.IsPlaying,model,TimeSpan.FromSeconds(20));
      Check(model.SelectedTrack!.Number==2&&sinks.Count==2&&sinks.All(s=>s.Finite&&s.Peak>.001),"One Play advances from ADM into normal FLAC without treating object tracks as speaker channels");
      model.ChooseSpatialInput=_=>Task.FromResult(SpatialOpenChoice.Spatial);await model.OpenAudioFilesAsync([small]);await model.EnableAdmPlaybackAsync();
      AudioNavigationService.HeadphoneOutputFactory=()=>new SpatialLabTests.SilentDevice();await model.TogglePlaybackAsync();
      var field=typeof(MainViewModel).GetField("_playbackControl",BindingFlags.Instance|BindingFlags.NonPublic)!;
      await Until(()=>field.GetValue(model)is PreviewPlaybackControl{HasStarted:true},model,TimeSpan.FromSeconds(10));
      var held=(PreviewPlaybackControl)field.GetValue(model)!;await model.TogglePlaybackAsync();await Until(()=>held.DevicePaused,model,TimeSpan.FromSeconds(3));await model.TogglePlaybackAsync();
      Check(ReferenceEquals(held,field.GetValue(model)),"ADM Pause / Play retains decoder, DSP and WASAPI output");
      await model.SeekAsync(10);await Until(()=>field.GetValue(model)is PreviewPlaybackControl{HasStarted:true},model,TimeSpan.FromSeconds(10));Check(model.PreviewSeconds>=10,"ADM main timeline seeks within the decoded PCM and metadata together");model.StopPlayback();await Task.Delay(100);
      string fixtureRoot=Path.GetDirectoryName(Path.GetFullPath(small))!;
      if(File.Exists(Path.Combine(fixtureRoot,"../e-drive-atmos/lamb-first30s.thd"))){
       string thd=Path.GetFullPath(Path.Combine(fixtureRoot,"../e-drive-atmos/lamb-first30s.thd"));var fi=new FileInfo(thd);
       var stream=new AudioStreamInfo{Index=0,Codec="truehd",Profile="Dolby TrueHD + Dolby Atmos",Channels=8,ChannelLayout="7.1",SampleRate=48000,BitDepth=24,StartTimeTicks=0};
       var pl=new PlaylistInfo{Id=1,DurationTicks=30*45000,ChapterStarts=[0],Clips=[new("00000",0,30*45000,0,File:new(thd,fi.Length,fi.LastWriteTimeUtc.Ticks,1440000,48000){Streams=[stream]})],TitleName="Small service test",Format=DiscFormat.AudioFile};
       var disc=new DiscAnalysis{Root=thd,DiscKey="fixture-truehd",Format=DiscFormat.AudioFile,AlbumTitle="Small service test",Playlists=[pl]};
       var service=new AtmosImportService(tools,probe,new AppLog(Path.Combine(root,"logs")));var row=new TrackRow{Number=1,StartTicks=0,EndTicks=30*45000,Title="Service fixture",IsChapter=true};
       var prepared=await service.PrepareAsync(disc,pl,stream,[row],null,CancellationToken.None);Check(AdmWave.Open(prepared).Channels==16,"Actual external demux + truehdd + streaming BW64 bridge succeeds through the production import service");
       long stamp=new FileInfo(prepared).LastWriteTimeUtc.Ticks;Check(await service.PrepareAsync(disc,pl,stream,[row],null,CancellationToken.None)==prepared&&new FileInfo(prepared).LastWriteTimeUtc.Ticks==stamp,"Second open reuses verified full-title cache without decoding again");
       model.ChooseSpatialInput=_=>Task.FromResult(SpatialOpenChoice.Standard);await model.OpenAudioFilesAsync([thd]);Check(model.CanPlay&&model.CurrentAdm is null&&model.SelectedStream!.Codec=="truehd"&&Math.Abs(model.PreviewMax-30)<.01,"Open audio file handles elementary TrueHD without a container duration and supports the normal presentation");
      }
      Save(window,Path.Combine(root,"main-adm.png"));
     }
     Check(errors.Count==0,"No real WPF dispatcher exceptions");SpatialJson.Save(Path.Combine(root,"results.json"),new{passed=checks.Count,checks,details});
    }
    var task=Tests();while(!task.IsCompleted)Pump(15);task.GetAwaiter().GetResult();
   }catch(Exception e){error=e;}finally{AudioNavigationService.HeadphoneOutputFactory=oldFactory;window?.Close();Application.Current?.Shutdown();}
  });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(error is not null)throw new InvalidDataException("ADM integration test failed",error);
 }
 static async Task Until(Func<bool> done,MainViewModel model,TimeSpan timeout){var w=Stopwatch.StartNew();while(!done()){if(w.Elapsed>timeout)throw new TimeoutException();model.AdvancePlaybackClock();await Task.Delay(15);}}
 static void Pump(int n){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(n)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
 static void Save(Window w,string path){w.UpdateLayout();var b=new RenderTargetBitmap((int)w.ActualWidth,(int)w.ActualHeight,96,96,PixelFormats.Pbgra32);b.Render(w);var e=new PngBitmapEncoder();e.Frames.Add(BitmapFrame.Create(b));using var f=File.Create(path);e.Save(f);}
 sealed class FastOutput:IHeadphoneOutput{
  public long SubmittedFrames{get;private set;}public long PlayedFrames=>SubmittedFrames;public double BufferMilliseconds=>10;public double StreamLatencyMilliseconds=>0;
  public bool HasStarted=>SubmittedFrames>0;public long EmptyBufferCount=>0;public bool SupportsPause=>true;public bool Disposed;public bool Finite=true;public double Peak;
  public void Pause(){}public void Resume(){}public void WaitForSpace(int frames,CancellationToken token)=>token.ThrowIfCancellationRequested();
  public void Write(float[] pcm,int frames){foreach(float v in pcm.AsSpan(0,frames*2)){Finite&=float.IsFinite(v);Peak=Math.Max(Peak,Math.Abs(v));}SubmittedFrames+=frames;}
  public void Drain(CancellationToken token)=>token.ThrowIfCancellationRequested();public void Dispose()=>Disposed=true;
 }
}
