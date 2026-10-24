using Disc2Flac;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;

internal static class BrirAtmosTests
{
    public static void Run(string output,string source,string manifest)
    {
        string root=Path.GetFullPath(output);Directory.CreateDirectory(root);Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME",root);
        Exception? failure=null;var checks=new List<string>();
        var thread=new Thread(()=>
        {
            MainViewModel? model=null;MainWindow? window=null;var factory=AudioNavigationService.HeadphoneOutputFactory;
            try
            {
                var app=new App();app.InitializeComponent();WpfTestApplication.SuppressStartupWindow(app);SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                void Check(bool ok,string name){if(!ok)throw new InvalidDataException(name);checks.Add(name);Console.WriteLine("PASS "+name);}
                void Complete(Task task){while(!task.IsCompleted)Pump(25);task.GetAwaiter().GetResult();}
                model=new MainViewModel();window=new MainWindow(model){ShowActivated=false};
                window.Loaded-=(RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler),window,typeof(MainWindow).GetMethod("Window_Loaded",BindingFlags.Instance|BindingFlags.NonPublic)!);
                window.Show();
                AudioNavigationService.HeadphoneOutputFactory=()=>new SpatialLabTests.SilentDevice();
                Complete(model.OpenAudioFilesAsync([Path.GetFullPath(source)]));Complete(model.EnableAdmPlaybackAsync());
                Check(model.IsAtmosStreamingEnabled,"Atmos BRIR: real progressive object source enabled");
                var brir=BrirLibrary.Prepare(Path.GetFullPath(manifest));
                bool rejected=false;try{Complete(model.ApplySpatialBrirAsync(brir));}catch(NotSupportedException){rejected=true;}
                Check(rejected,"Atmos BRIR: applying fixed speaker responses to moving Objects is refused");
                Complete(model.ChangeAtmosPresentationAsync(model.AtmosPreviewChoices.Single(c=>c.Value==AtmosPresentation.Surround51)));
                Complete(model.ApplySpatialBrirAsync(brir));Check(model.CurrentAdmScene?.Sources.Length==6&&HeadphoneSession.Current.Options.Method==HeadphoneMethod.MeasuredBrir,"Atmos BRIR: explicit 5.1 presentation accepts all six measured speaker routes");
                Complete(model.TogglePlaybackAsync());
                var end=DateTime.UtcNow.AddSeconds(15);
                PreviewPlaybackControl? Control()=>(PreviewPlaybackControl?)typeof(MainViewModel).GetField("_playbackControl",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(model);
                while(Control()?.HasStarted!=true&&DateTime.UtcNow<end)Pump(25);
                Check(model.IsPlaying&&Control()?.HasStarted==true,"Atmos BRIR: real 5.1 PCM passes through measured convolution to the main device path");model.PausePlayback();
                var input=model.PrepareSpatialInputAsync(0,5,CancellationToken.None);Complete(input);
                var snapshot=new SpatialRenderSnapshot(HeadphoneSession.Current.Options,new(0,0,0),new(StereoMixSettings.Default,null),.3,brir.Hash);
                var render=SpatialRendering.RenderAsync(input.Result,snapshot,true,CancellationToken.None);Complete(render);
                Check(render.Result.Peak>0&&render.Result.ClippedSamples==0&&render.Result.Snapshot.Brir?.Hash==brir.Hash,"Atmos BRIR: progressive 5.1 offline export uses the same measured renderer and reproducibility metadata");
                Complete(model.ChangeAtmosPresentationAsync(model.AtmosPreviewChoices.Single(c=>c.Value==AtmosPresentation.Objects)));
                Check(HeadphoneSession.Current.Options.Method==HeadphoneMethod.SteamAudio&&model.CurrentAdmScene!.Sources.Length==16,"Atmos BRIR: returning to Objects restores HRTF and positional sources instead of misapplying BRIR");
                SpatialJson.Save(Path.Combine(root,"results.json"),new{passed=checks.Count,checks});
            }
            catch(Exception ex){failure=ex;}
            finally{model?.PausePlayback();if(model is not null){try{var disable=model.DisableAdmPlaybackAsync();while(!disable.IsCompleted)Pump(20);disable.GetAwaiter().GetResult();}catch{}}AudioNavigationService.HeadphoneOutputFactory=factory;window?.Close();Application.Current?.Shutdown();}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure is not null)throw new InvalidDataException("Atmos BRIR verification failed",failure);
    }
    static void Pump(int ms){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
}
