using Disc2Flac;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

internal static class SpatialWindowLifecycleTests
{
    public static void Run(string output, string? fullAdm = null)
    {
        string root = Path.GetFullPath(output); Directory.CreateDirectory(root); Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", root);
        Exception? failure = null; List<string> checks = []; var thread = new Thread(() =>
        {
            MainViewModel? model = null; MainWindow? main = null; SpatialLabWindow? lab = null;
            TaskCompletionSource? pending = null; Task? pendingJob = null;
            try
            {
                var app = new App(); app.InitializeComponent(); WpfTestApplication.SuppressStartupWindow(app);
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); var errors = new List<Exception>();
                app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception); e.Handled = true; };
                void Check(bool condition, string name) { if (!condition) throw new InvalidDataException(name); checks.Add(name); Console.WriteLine("PASS " + name); }
                object? Field(object target, string name) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);
                void Open()
                {
                    ((Button)main!.FindName("OpenSpatialLabButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    lab = (SpatialLabWindow?)Field(main, "_spatialLab");
                }
                Task StartJob(SpatialLabWindow window, TaskCompletionSource completion) => (Task)typeof(SpatialLabWindow).GetMethod("Job", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [new Func<CancellationToken, Task>(_ => completion.Task), false])!;
                async Task Tests()
                {
                    model = new MainViewModel(); main = new MainWindow(model) { ShowActivated = false };
                    main.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), main, typeof(MainWindow).GetMethod("Window_Loaded", BindingFlags.NonPublic | BindingFlags.Instance)!);
                    main.Show(); Open(); Check(lab is { IsVisible: true }, "Main button opens Spatial Lab");
                    pending = new(TaskCreationOptions.RunContinuationsAsynchronously); pendingJob = StartJob(lab!, pending); await Task.Delay(20);
                    Check(!((UIElement)main.Content).IsEnabled, "A running preparation owns the main interaction lock");
                    lab!.Close();
                    Check(((UIElement)main.Content).IsEnabled, "Closing a busy Lab immediately restores main controls even before a worker unwinds");
                    Open(); Check(lab is { IsVisible: true }, "Main button reopens a new Lab while the previous worker finishes");
                    var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var nextJob = StartJob(lab!, next); await Task.Delay(20);
                    pending.SetResult(); await pendingJob; pendingJob = null;
                    Check(!((UIElement)main.Content).IsEnabled, "Completion of an old window never releases a newer window's interaction lock");
                    next.SetResult(); await nextJob;
                    Check(((UIElement)main.Content).IsEnabled, "The current worker restores its own interaction lock");
                    var old = lab; lab!.WindowState = WindowState.Minimized; Open();
                    Check(ReferenceEquals(old, lab) && lab!.WindowState == WindowState.Normal, "Reopening restores an existing minimized Lab");
                    string demo = Path.Combine(root, "original-adm.wav"); AdmDemo.Create(demo);
                    await model.OpenAudioFilesAsync([fullAdm is null ? demo : Path.GetFullPath(fullAdm)]);
                    bool activated = await model.EnableAdmPlaybackAsync(); await Task.Delay(50);
                    Console.WriteLine($"Source activation={activated}, available={model.IsAdmAvailable}, busy={model.IsBusy}, ADM={model.CurrentAdm is not null}, tracks={model.Tracks.Count}, labEnabled={((TabControl)lab!.FindName("Pages")).IsEnabled}, status={model.Status}");
                    Check(model.IsAdmPlaybackEnabled && ((TabControl)lab!.FindName("Pages")).IsEnabled, "Source changes leave the Lab enabled after explicit ADM activation");
                    // Use the actual chaptered input when supplied; otherwise create UI chapter ranges over the original demo.
                    if (model.Tracks.Count < 2) { var original = model.SelectedTrack!; model.Tracks.Add(new TrackRow { Number = 2, StartTicks = original.StartTicks + 45000, EndTicks = original.EndTicks, Title = "Second test chapter", IsChapter = true }); }
                    for (int i = 0; i < 12; i++)
                    {
                        var clock = Stopwatch.StartNew(); model.SelectedTrack = model.Tracks[i % model.Tracks.Count]; await Task.Delay(20);
                        Check(clock.Elapsed.TotalSeconds < 1 && ((TabControl)lab!.FindName("Pages")).IsEnabled, "Chapter change remains responsive " + i);
                        var prior = lab; lab!.Close(); Open();
                        Check(lab is { IsVisible: true } && !ReferenceEquals(prior, lab), "Close / reopen after chapter change " + i);
                    }
                    ((ComboBox)lab!.FindName("RoomMode")).SelectedIndex = 1; lab.Close(); Open(); await Task.Delay(450);
                    Check(lab is { IsVisible: true } && ((UIElement)main.Content).IsEnabled, "Closing during pending reflection edits does not change the reopened window");
                    Check(errors.Count == 0, "Track changes, cancellation, close and reopen produce no dispatcher errors");
                    SpatialJson.Save(Path.Combine(root, "results.json"), new { passed = checks.Count, checks });
                }
                var task = Tests(); while (!task.IsCompleted) Pump(10); task.GetAwaiter().GetResult();
            }
            catch (Exception ex) { failure = ex; }
            finally { pending?.TrySetResult(); if (pendingJob is not null) while (!pendingJob.IsCompleted) Pump(10); model?.PausePlayback(); lab?.Close(); main?.Close(); Application.Current?.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidDataException("Spatial window lifecycle verification failed", failure);
    }
    static void Pump(int ms) { var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) }; timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); }
}
