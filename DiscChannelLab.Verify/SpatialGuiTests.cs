using Disc2Flac;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class SpatialGuiTests
{
    public static void Run(string output, string admPath, string fixtureRoot)
    {
        string root = Path.GetFullPath(output); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME",root);
        Exception? failure=null; var checks=new List<string>();
        var thread=new Thread(()=>
        {
            SpatialLabWindow? window=null;
            try
            {
                var app=new App();app.InitializeComponent();WpfTestApplication.SuppressStartupWindow(app);ThemeService.SetDark(true);
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var errors=new List<Exception>();app.DispatcherUnhandledException+=(_,e)=>{errors.Add(e.Exception);e.Handled=true;};
                var library=new SofaLibrary();library.Entries.Add(SofaLibrary.Inspect(Path.Combine(fixtureRoot,"MIT_KEMAR_large_pinna.sofa"),CancellationToken.None));library.Save();
                var model=new MainViewModel();window=new SpatialLabWindow(model){ShowActivated=false};window.Show();Pump(250);
                void Check(bool ok,string name){if(!ok)throw new InvalidDataException(name);checks.Add(name);Console.WriteLine("PASS "+name);}
                Check(window.FindName("Pages") is TabControl {Items.Count:5},"GUI: all four phases and measured BRIR have accessible tabs");
                Check(((DataGrid)window.FindName("LibraryGrid")).Items.Count==2,"GUI: registered SOFA metadata appears in real library grid");
                Save(window,Path.Combine(root,"01-sofa-library.png"));
                var pages=(TabControl)window.FindName("Pages");pages.SelectedIndex=1;Pump(150);Save(window,Path.Combine(root,"02-comparison-export.png"));
                pages.SelectedIndex=2;Pump(150);
                ((TextBox)window.FindName("RoomWidth")).Text="8";
                typeof(SpatialLabWindow).GetMethod("ResetRoom_Click",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[new object(),new RoutedEventArgs()]);
                Check(((TextBox)window.FindName("RoomWidth")).Text=="4.5","GUI: reset room restores the preset");
                Save(window,Path.Combine(root,"03-early-reflections.png"));
                pages.SelectedItem=window.FindName("AdmSceneTab");
                var load=model.OpenAudioFilesAsync([admPath]);
                while(!load.IsCompleted)Pump(20);load.GetAwaiter().GetResult();
                var enable=model.EnableAdmPlaybackAsync();while(!enable.IsCompleted)Pump(20);enable.GetAwaiter().GetResult();Pump(200);pages.SelectedItem=window.FindName("AdmSceneTab");
                Check(((DataGrid)window.FindName("ObjectsGrid")).Items.Count==3,"GUI: real ADM programme and objects populate the inspector");
                model.SeekAsync(8).GetAwaiter().GetResult();Pump(150);
                Check(((TextBlock)window.FindName("SceneDetails")).Text.Contains("3/3 active"),"GUI: source positions follow the actual sample timeline");
                Save(window,Path.Combine(root,"04-adm-scene.png"));
                typeof(SpatialLabWindow).GetMethod("SaveSettings",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[]);
                Check(File.Exists(AppDataPaths.Current.PreferencePath("profiles/spatial-lab.json")),"GUI: lab settings save independently");
                Check(errors.Count==0,"GUI: no dispatcher/rendering exceptions in real WPF windows");
                SpatialJson.Save(Path.Combine(root,"results.json"),new{passed=checks.Count,checks});
            }
            catch(Exception ex){failure=ex;}
            finally{window?.Close();Application.Current?.Shutdown();}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure is not null)throw new InvalidDataException("Spatial GUI tests failed",failure);
    }
    static void Pump(int ms){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
    static void Save(Window window,string path)
    {
        window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);
    }
}
