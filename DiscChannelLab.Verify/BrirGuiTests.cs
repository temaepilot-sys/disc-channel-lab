using Disc2Flac;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class BrirGuiTests
{
    public static void Run(string output,string fixture,string? brirProfile = null)
    {
        string root=Path.GetFullPath(output),data=Path.GetFullPath(fixture);Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME",root);
        Exception? failure=null;var checks=new List<string>();
        var thread=new Thread(()=>
        {
            MainWindow? main=null;SpatialLabWindow? lab=null;HeadphonesWindow? phones=null;MainViewModel? model=null;
            var factory=AudioNavigationService.HeadphoneOutputFactory;
            try
            {
                var app=new App();app.InitializeComponent();WpfTestApplication.SuppressStartupWindow(app);ThemeService.SetDark(true);
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var errors=new List<Exception>();app.DispatcherUnhandledException+=(_,e)=>{errors.Add(e.Exception);e.Handled=true;};
                void Check(bool ok,string name){if(!ok)throw new InvalidDataException(name);checks.Add(name);Console.WriteLine("PASS "+name);}
                void Complete(Task task){while(!task.IsCompleted)Pump(20);task.GetAwaiter().GetResult();}
                model=new MainViewModel();main=new MainWindow(model){ShowActivated=false};
                main.Loaded-=(RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler),main,typeof(MainWindow).GetMethod("Window_Loaded",BindingFlags.Instance|BindingFlags.NonPublic)!);
                main.Show();Pump(100);
                Check(model.SelectedPreviewMode==PreviewAudioMode.Stereo,"GUI: new session still starts in ordinary stereo");
                Complete(model.OpenAudioFilesAsync([Path.Combine(data,"brir-render.flac")]));
                lab=new SpatialLabWindow(model){Owner=main,ShowActivated=false};lab.Show();Pump(100);
                var pages=(TabControl)lab.FindName("Pages");pages.SelectedItem=lab.FindName("BrirTab");Pump(100);
                Check(pages.Items.Cast<TabItem>().Select(t=>t.Header.ToString()).SequenceEqual(new[]{"1 · HRTF Library","2 · Compare / Export","3 · HRIR / Reflections","4 · Measured BRIR","5 · ADM scene"}),
                    "GUI: HRIR and BRIR are adjacent tabs 3 / 4, with ADM scene on tab 5");
                string selectedProfile=Path.GetFullPath(brirProfile??Path.Combine(data,"synthetic-test.brir.json"));
                typeof(SpatialLabWindow).GetMethod("ReadBrir",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(lab,[selectedProfile]);Pump(100);
                Check(((DataGrid)lab.FindName("BrirGrid")).Items.Count==BrirLibrary.ReadProfile(selectedProfile).Routes.Length,"GUI: editable explicit speaker mapping is visible");
                AudioNavigationService.HeadphoneOutputFactory=()=>new SpatialLabTests.SilentDevice();
                model.SelectedPreviewMode=PreviewAudioMode.HeadphonesHrtf;
                Complete(model.TogglePlaybackAsync());Pump(200);
                var controlField=typeof(MainViewModel).GetField("_playbackControl",BindingFlags.Instance|BindingFlags.NonPublic)!;
                PreviewPlaybackControl? Control()=>(PreviewPlaybackControl?)controlField.GetValue(model);
                Wait(()=>Control()?.HasStarted==true);
                var priorControl=Control();
                ((CheckBox)lab.FindName("BrirConfirmed")).IsChecked=false;
                Call(lab,"ApplyBrir_Click");Wait(()=>pages.IsEnabled&&!((TextBox)lab.FindName("StatusText")).Text.StartsWith("Loading"));
                Check(model.IsPlaying&&ReferenceEquals(Control(),priorControl)&&HeadphoneSession.Current.Options.Method==HeadphoneMethod.SteamAudio,
                    "GUI: failed BRIR validation leaves existing HRTF audio playing with the same output session");
                ((CheckBox)lab.FindName("BrirConfirmed")).IsChecked=true;
                model.AdvancePlaybackClock();double heldPosition=model.PreviewSeconds;
                Call(lab,"ApplyBrir_Click");Wait(()=>!((TextBox)lab.FindName("StatusText")).Text.StartsWith("Loading")&&model.SelectedPreviewMode==PreviewAudioMode.HeadphonesBrir);
                Wait(()=>pages.IsEnabled&&model.IsPlaying&&Control()?.HasStarted==true);
                Check(model.SelectedPreviewMode==PreviewAudioMode.HeadphonesBrir,"GUI: validate/apply selects actual BRIR main-player renderer");
                Check(model.IsPlaying&&!ReferenceEquals(Control(),priorControl),"GUI: successful apply during HRTF playback rebuilds DSP and automatically resumes BRIR audio");
                model.AdvancePlaybackClock();Check(model.PreviewSeconds>=heldPosition-.1&&model.PreviewSeconds<heldPosition+1.5,
                    "GUI: applying BRIR preserves the track position");
                Check(((TextBlock)lab.FindName("ActiveHrtfText")).Text.Contains("Measured BRIR")&&((TextBlock)lab.FindName("ActiveRoomText")).Text.Contains("bypassed"),"GUI: active renderer and HRIR/reflection bypass are explicit");
                Save(lab,Path.Combine(root,"01-measured-brir-dark.png"));
                AudioNavigationService.HeadphoneOutputFactory=()=>new SpatialLabTests.SilentDevice();
                Check(model.IsPlaying,"GUI: main player uses BRIR with the ordinary FLAC input");
                model.PausePlayback();Pump(80);Check(!model.IsPlaying,"GUI: main Pause retains BRIR playback session");
                Call(lab,"ApplyBrir_Click");Wait(()=>pages.IsEnabled&&((TextBox)lab.FindName("StatusText")).Text.StartsWith("Measured BRIR applied"));
                Check(!model.IsPlaying,"GUI: applying BRIR while paused does not start playback unexpectedly");
                Complete(model.TogglePlaybackAsync());Pump(150);Check(model.IsPlaying,"GUI: main Play resumes BRIR after Pause");model.PausePlayback();
                phones=new HeadphonesWindow(model){Owner=main,ShowActivated=false};phones.Show();Pump(100);
                Check(((ComboBox)phones.FindName("MethodCombo")).SelectedIndex==2&&((TabControl)phones.FindName("SettingsTabs")).SelectedIndex==2,"GUI: Headphones correctly shows the separate BRIR tab");
                Check(!((CheckBox)phones.FindName("UseTracker")).IsEnabled&&!((StackPanel)phones.FindName("ManualControls")).IsEnabled,"GUI: tracking/manual rotation are disabled for fixed measured BRIR");
                Save(phones,Path.Combine(root,"02-headphones-brir.png"));phones.Close();phones=null;
                pages.SelectedItem=lab.FindName("ComparisonTab");((TextBox)lab.FindName("IntervalLength")).Text="5";((CheckBox)lab.FindName("CompareBrir")).IsChecked=true;
                Call(lab,"Compare_Click");Wait(()=>((TextBox)lab.FindName("ComparisonText")).Text.Contains("identical source frames"));
                Check(((TextBox)lab.FindName("ComparisonText")).Text.Contains("(BRIR)")&&((TextBox)lab.FindName("ComparisonText")).Text.Contains("Loudness matched"),"GUI: real HRIR / BRIR preparation produces level-matched comparison");
                Save(lab,Path.Combine(root,"03-hrir-brir-comparison.png"));
                Call(lab,"PureHrir_Click");Wait(()=>HeadphoneSession.Current.Options.Method==HeadphoneMethod.SteamAudio);
                Check(!HeadphoneSession.Current.Options.Room.Enabled,"GUI: return to Pure HRIR removes added room processing");
                ThemeService.SetDark(false);pages.SelectedItem=lab.FindName("BrirTab");Pump(100);Save(lab,Path.Combine(root,"04-measured-brir-light.png"));
                Check(File.Exists(BrirLibrary.LastProfilePath),"GUI: BRIR mappings are remembered separately from disc metadata");
                Check(errors.Count==0,"GUI: real WPF windows, dark/light themes and async commands without dispatcher errors");
                SpatialJson.Save(Path.Combine(root,"results.json"),new{passed=checks.Count,checks});
            }
            catch(Exception ex){failure=ex;}
            finally{AudioNavigationService.HeadphoneOutputFactory=factory;model?.PausePlayback();phones?.Close();lab?.Close();main?.Close();Application.Current?.Shutdown();}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure is not null)throw new InvalidDataException("BRIR GUI verification failed",failure);
    }
    static void Call(Window window,string method)=>window.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[new object(),new RoutedEventArgs()]);
    static void Wait(Func<bool> predicate){var end=DateTime.UtcNow.AddSeconds(25);while(!predicate()&&DateTime.UtcNow<end)Pump(25);if(!predicate())throw new TimeoutException("BRIR GUI command did not complete.");}
    static void Pump(int ms){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
    static void Save(Window window,string path){window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);}
}
