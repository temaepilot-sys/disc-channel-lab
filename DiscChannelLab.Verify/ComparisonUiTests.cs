using Disc2Flac;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class ComparisonUiTests
{
    public static void Run(string output,string audioFile,string brirProfile)
    {
        string root=Path.GetFullPath(output);Directory.CreateDirectory(root);Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME",root);
        var checks=new List<string>();Exception? failure=null;
        var thread=new Thread(()=>
        {
            MainViewModel? model=null;MainWindow? main=null;SpatialLabWindow? lab=null;var factory=AudioNavigationService.HeadphoneOutputFactory;
            var outputs=new List<CapturedOutput>();
            try
            {
                var app=new App();app.InitializeComponent();WpfTestApplication.SuppressStartupWindow(app);ThemeService.SetDark(true);
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var errors=new List<Exception>();app.DispatcherUnhandledException+=(_,e)=>{errors.Add(e.Exception);e.Handled=true;};
                void Check(bool ok,string name){if(!ok)throw new InvalidDataException(name);checks.Add(name);Console.WriteLine("PASS "+name);}
                void Complete(Task task){while(!task.IsCompleted)Pump(20);task.GetAwaiter().GetResult();}
                void Until(Func<bool> test,string name,int timeout=15000){var end=DateTime.UtcNow.AddMilliseconds(timeout);while(!test()&&DateTime.UtcNow<end)Pump(20);if(!test())throw new TimeoutException(name+" · "+((TextBlock)lab!.FindName("ComparisonFeedback")).Text+" · "+model?.Status);}
                Button Button(string name)=>(Button)lab!.FindName(name);
                void Click(string name){var b=Button(name);Check(b.IsEnabled,"Clickable: "+name);b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));}
                string Feedback()=>((TextBlock)lab!.FindName("ComparisonFeedback")).Text;
                TabControl Pages()=>(TabControl)lab!.FindName("Pages");
                bool Playing()=>typeof(SpatialLabWindow).GetField("comparisonStop",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(lab)is not null;
                void Prepared(){Until(()=>Pages().IsEnabled&&Button("ComparisonPlayAButton").IsEnabled&&Button("ComparisonPlayBButton").IsEnabled,"prepared");}
                model=new MainViewModel();main=new MainWindow(model){ShowActivated=false};
                main.Loaded-=(RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler),main,typeof(MainWindow).GetMethod("Window_Loaded",BindingFlags.Instance|BindingFlags.NonPublic)!);main.Show();
                lab=new SpatialLabWindow(model){Owner=main,ShowActivated=false};lab.Show();Pages().SelectedItem=lab.FindName("ComparisonTab");Pump(100);
                AudioNavigationService.HeadphoneOutputFactory=()=>{var o=new CapturedOutput();outputs.Add(o);return o;};
                Check(!Button("ComparisonPlayAButton").IsEnabled&&!Button("ComparisonPlayBButton").IsEnabled,"Not prepared: both playback buttons are disabled");
                Click("ComparisonPrepareButton");Until(()=>Pages().IsEnabled,"no-source error");
                Check(Feedback().Contains("Select a playable track")&&!Button("ComparisonPlayAButton").IsEnabled,"No source: visible error next to buttons and no stale playback");
                Complete(model.OpenAudioFilesAsync([Path.GetFullPath(audioFile)]));
                ((TextBox)lab.FindName("IntervalLength")).Text="5";Click("ComparisonPrepareButton");Prepared();
                Check(Feedback().Contains("SAME HRIR"),"Identical A/B selection explicitly explains why no audible difference is expected");
                Check(outputs.Count==0,"Prepare A/B renders without starting an audio device");
                Click("ComparisonPlayAButton");Until(()=>outputs.Count==1&&outputs[0].SignalSamples>8000,"A audio");
                Until(()=>((TextBlock)lab.FindName("ComparisonPosition")).Text.StartsWith("Playing A"),"A clock");
                Check(Pages().IsEnabled&&Button("ComparisonPlayBButton").IsEnabled&&Button("ComparisonStopButton").IsEnabled,"During A: B and Stop remain clickable");
                Save(lab,Path.Combine(root,"01-playing-a.png"));
                var switchWatch=Stopwatch.StartNew();Click("ComparisonPlayBButton");Until(()=>outputs.Count==2&&outputs[1].SignalSamples>8000,"switch B");
                Check(outputs[0].Disposed&&switchWatch.ElapsedMilliseconds<2000,"Clicking B stops A and delivers B without overlapping output sessions");
                Click("ComparisonPlayAButton");Until(()=>outputs.Count==3&&outputs[2].SignalSamples>8000,"switch back A");
                Check(outputs[1].Disposed,"Clicking A during B restarts A at its prepared interval start");
                Click("ComparisonStopButton");Until(()=>!Playing(),"stop");
                Check(((UIElement)main.Content).IsEnabled&&Button("ComparisonPlayAButton").IsEnabled,"Stop restores main controls and keeps prepared A/B ready");
                Check(((TextBlock)lab.FindName("ComparisonPosition")).Text.StartsWith("Stopped "),"Stopped audition no longer displays a misleading Playing position label");
                Click("ComparisonPlayBButton");Until(()=>outputs.Count==4&&outputs[3].Disposed&&!Playing(),"natural finish",10000);
                Check(Button("ComparisonPlayAButton").IsEnabled&&Button("ComparisonPlayBButton").IsEnabled,"After the interval finishes, A/B can be played again");
                ((TextBox)lab.FindName("IntervalStart")).Text="9999";
                Check(!Button("ComparisonPlayAButton").IsEnabled,"Editing the interval invalidates old prepared playback");
                Complete(model.TogglePlaybackAsync());Until(()=>outputs.Count==5&&outputs[4].SignalSamples>8000,"main audio");
                Click("ComparisonPrepareButton");Until(()=>Pages().IsEnabled,"invalid start");
                Check(Feedback().Contains("Start is beyond")&&model.IsPlaying&&!outputs[4].Disposed,"Invalid preparation reports the input error without stopping main playback");
                Save(lab,Path.Combine(root,"02-input-error.png"));model.PausePlayback();
                ((TextBox)lab.FindName("IntervalStart")).Text="0";
                var library=(SofaLibrary)typeof(SpatialLabWindow).GetField("library",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(lab)!;
                var invalid=HrtfEntry.BuiltIn with{Id="unused-unsupported",Name="Unsupported B fixture",Status="Unsupported",Diagnostic="B must not be validated for HRIR / BRIR"};
                library.Entries.Add(invalid);Call(lab,"RefreshLibrary");((ComboBox)lab.FindName("HrtfB")).SelectedItem=invalid;
                Call(lab,"ReadBrir",Path.GetFullPath(brirProfile));((CheckBox)lab.FindName("CompareBrir")).IsChecked=true;
                Check(!((ComboBox)lab.FindName("HrtfB")).IsEnabled&&!((TextBox)lab.FindName("FixedYaw")).IsEnabled,"BRIR mode visibly disables unused HRIR B and fixes the head orientation");
                Click("ComparisonPrepareButton");Prepared();
                var comparison=(ComparisonAudio)typeof(SpatialLabWindow).GetField("comparison",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(lab)!;
                Check(comparison.A.Snapshot.Options.Method==HeadphoneMethod.SteamAudio&&comparison.B.Snapshot.Options.Method==HeadphoneMethod.MeasuredBrir,"Real BRIR comparison prepares successfully while unused HRIR B is unsupported");
                Check(!SHA256.HashData(File.ReadAllBytes(comparison.A.PcmPath)).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(comparison.B.PcmPath))),"Prepared HRIR and real D1 BRIR contain different stereo PCM for the same interval");
                int before=outputs.Count;Click("ComparisonPlayAButton");Until(()=>outputs.Count==before+1&&outputs[^1].SignalSamples>8000,"HRIR A");
                Click("ComparisonPlayBButton");Until(()=>outputs.Count==before+2&&outputs[^1].SignalSamples>8000,"real BRIR B");
                Until(()=>((TextBlock)lab.FindName("ComparisonPosition")).Text.StartsWith("Playing B"),"B clock");
                Check(outputs[^2].Disposed&&outputs[^1].Finite,"Actual HRIR A → measured D1 BRIR B button switch delivers finite stereo output");
                Save(lab,Path.Combine(root,"03-playing-brir-b.png"));Click("ComparisonStopButton");Until(()=>!Playing(),"stop BRIR");
                ((CheckBox)lab.FindName("CompareRoom")).IsChecked=true;
                Check(((CheckBox)lab.FindName("CompareBrir")).IsChecked==false,"Room comparison and BRIR comparison are mutually exclusive");
                Call(lab,"ReadRoom",new SpatialRoomOptions{Mode="Studio"});Click("ComparisonPrepareButton");Prepared();
                Check(((TextBox)lab.FindName("ComparisonText")).Text.Contains("Studio"),"Room OFF / ON prepares without validating unused HRIR B");
                ((CheckBox)lab.FindName("CompareRoom")).IsChecked=false;Click("ComparisonPrepareButton");Until(()=>Pages().IsEnabled,"unsupported B error");
                Check(Feedback().Contains("B must not be validated")&&!Button("ComparisonPlayAButton").IsEnabled,"HRIR-vs-HRIR correctly reports an actually used unsupported B");
                ((CheckBox)lab.FindName("CompareBrir")).IsChecked=true;Click("ComparisonPrepareButton");Prepared();
                ThemeService.SetDark(false);Pump(100);Save(lab,Path.Combine(root,"04-comparison-light.png"));
                Complete(model.OpenAudioFilesAsync([Path.GetFullPath("tests/spatial/SpatialLab-original-ADM-demo.wav")]));Complete(model.EnableAdmPlaybackAsync());
                ((CheckBox)lab.FindName("CompareBrir")).IsChecked=false;((ComboBox)lab.FindName("HrtfB")).SelectedItem=library.Entries[0];
                ((TextBox)lab.FindName("IntervalStart")).Text="2";Click("ComparisonPrepareButton");Prepared();
                before=outputs.Count;Click("ComparisonPlayBButton");Until(()=>outputs.Count>before&&outputs[^1].SignalSamples>8000,"close playing B");
                Until(()=>((ProgressBar)lab.FindName("SceneTime")).Value>2.05,"ADM audition scene clock");
                Check(model.CurrentAdmSeconds<2&&((ProgressBar)lab.FindName("SceneTime")).Value>2.05,"ADM scene follows the prepared audition clock while the main transport remains held");
                lab.Close();Until(()=>outputs[^1].Disposed&&((UIElement)main.Content).IsEnabled,"close releases audio");
                Check(outputs.Where((o,i)=>i!=4).All(o=>o.Disposed)&&errors.Count==0,"Closing the comparison view releases every audition device and restores main controls without WPF errors");
                SpatialJson.Save(Path.Combine(root,"results.json"),new{passed=checks.Count,checks,devices=outputs.Count});
            }
            catch(Exception ex){failure=ex;}
            finally{model?.PausePlayback();lab?.Close();Pump(200);main?.Close();AudioNavigationService.HeadphoneOutputFactory=factory;Application.Current?.Shutdown();}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure is not null)throw new InvalidDataException("Comparison button verification failed",failure);
    }
    static void Call(Window window,string method,params object[] args)=>window.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,args);
    static void Pump(int ms){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
    static void Save(Window window,string path){window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);}
    sealed class CapturedOutput : IHeadphoneOutput
    {
        readonly SpatialLabTests.SilentDevice device=new();int signals,disposed;
        public int SignalSamples=>Volatile.Read(ref signals);public bool Disposed=>Volatile.Read(ref disposed)!=0;public bool Finite{get;private set;}=true;
        public long SubmittedFrames=>device.SubmittedFrames;public long PlayedFrames=>device.PlayedFrames;public double BufferMilliseconds=>device.BufferMilliseconds;public double StreamLatencyMilliseconds=>device.StreamLatencyMilliseconds;public bool HasStarted=>device.HasStarted;public long EmptyBufferCount=>device.EmptyBufferCount;public bool SupportsPause=>true;
        public void Pause()=>device.Pause();public void Resume()=>device.Resume();public void WaitForSpace(int n,CancellationToken t)=>device.WaitForSpace(n,t);
        public void Write(float[] samples,int n){int signal=0;for(int i=0;i<n*2;i++){Finite&=float.IsFinite(samples[i]);if(Math.Abs(samples[i])>1e-8)signal++;}Interlocked.Add(ref signals,signal);device.Write(samples,n);}
        public void Drain(CancellationToken t)=>device.Drain(t);public void Dispose(){device.Dispose();Volatile.Write(ref disposed,1);}
    }
}
