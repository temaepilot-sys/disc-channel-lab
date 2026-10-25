using Disc2Flac;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;

internal static class SaveNotificationTests
{
    public static void Run(string output)
    {
        string root = Path.GetFullPath(output); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("DISCCHANNEL_DATA_HOME", Path.Combine(root, "profile"));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainViewModel? model = null; MainWindow? main = null; Window? child = null; HeadphonesWindow? headphones = null;
            try
            {
                var app = new App(); app.InitializeComponent(); WpfTestApplication.SuppressStartupWindow(app);
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var errors = new List<Exception>(); app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception); e.Handled = true; };
                var checks = new List<string>();
                void Check(bool ok, string name) { if (!ok) throw new InvalidDataException(name); checks.Add(name); Console.WriteLine("PASS " + name); }
                Task<bool> SaveAdm(string path) => (Task<bool>)typeof(MainWindow).GetMethod("SaveAdmAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, [path])!;
                async Task Tests()
                {
                    await Task.Yield(); // Start the WPF dispatcher before any synchronous file save can show a modal dialog.
                    LanguageService.Instance.SetJapanese(false);
                    model = new MainViewModel(); main = new MainWindow(model) { ShowActivated = false };
                    main.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), main, typeof(MainWindow).GetMethod("Window_Loaded", BindingFlags.NonPublic | BindingFlags.Instance)!);
                    main.Show();
                    Check(!await model.SaveDiagnosticsAsync(Path.Combine(root, "no-source.json")), "No source never reports a diagnostic save as completed");
                    string original = Path.Combine(root, "original-adm.wav"); AdmDemo.Create(original);
                    byte[] hash = SHA256.HashData(File.ReadAllBytes(original));
                    await model.OpenAudioFilesAsync([original]); await model.EnableAdmPlaybackAsync();
                    string english = Path.Combine(root, "english-adm.wav");
                    using (var dialog = new MusicFolderTests.CompletionDialogProbe(main))
                    {
                        Check(await SaveAdm(english), "The main-window ADM save command completes successfully");
                        Check(dialog.Messages.Count == 1 && dialog.Messages[0].Contains("Position-aware ADM WAV export completed.") && dialog.Messages[0].Contains(english),
                            "Real ADM completion dialog contains the English notification and destination");
                        Check(SHA256.HashData(File.ReadAllBytes(english)).SequenceEqual(hash) && AdmWave.Open(english).Channels == 3,
                            "Completion is shown after the final readable ADM WAV preserves all source bytes");
                        Check(!await SaveAdm(english) && dialog.Messages.Count == 1 && SHA256.HashData(File.ReadAllBytes(english)).SequenceEqual(hash),
                            "An existing destination is preserved without a false completion dialog");
                        string blocked = Path.Combine(root, "blocked"); File.WriteAllText(blocked, "keep");
                        Check(!await SaveAdm(Path.Combine(blocked, "failed.wav")) && dialog.Messages.Count == 1,
                            "A failed ADM write produces no success notification");
                        Check(!Directory.GetFiles(root, "*.partial", SearchOption.AllDirectories).Any(), "Failed ADM save leaves no partial export");
                    }
                    child = new Window { Owner = main, Title = "Save notification ownership", Width = 320, Height = 140 };
                    child.Show(); child.Activate();
                    Check(child.IsActive, "An owned tool window is active during the next ADM save");
                    LanguageService.Instance.SetJapanese(true);
                    string japanese = Path.Combine(root, "japanese-adm.wav");
                    using (var dialog = new MusicFolderTests.CompletionDialogProbe(child))
                    {
                        Check(await SaveAdm(japanese) && dialog.Messages.Count == 1 && dialog.Messages[0].Contains("位置情報付きADM WAVの保存が完了しました。")
                            && dialog.Messages[0].Contains(japanese), "Japanese ADM notification belongs to the active tool window, rather than hiding behind it");
                    }
                    child.Close(); child = null;
                    Check(await model.SaveDiagnosticsAsync(Path.Combine(root, "diagnostics.json")) && File.Exists(Path.Combine(root, "diagnostics.json")),
                        "Diagnostic success is returned only after the report is written");
                    Check(!await model.SaveDiagnosticsAsync(Path.Combine(root, "blocked", "diagnostics.json")), "Diagnostic write failures do not return success");
                    var session = HeadphoneSession.Current; Check(session.Save(), "Headphone settings can be persisted before notification");
                    string preference = AppDataPaths.Current.PreferencePath("headphones.json");
                    headphones = new HeadphonesWindow(model) { Owner = main, ShowActivated = false }; headphones.Show();
                    using (var dialog = new MusicFolderTests.CompletionDialogProbe(headphones))
                    {
                        File.SetAttributes(preference, File.GetAttributes(preference) | FileAttributes.ReadOnly);
                        try
                        {
                            typeof(HeadphonesWindow).GetMethod("Save_Click", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(headphones, [headphones, new RoutedEventArgs()]);
                            Check(dialog.Messages.Count == 0 && session.TrackerError is not null, "A failed explicit settings save never claims success");
                        }
                        finally { File.SetAttributes(preference, File.GetAttributes(preference) & ~FileAttributes.ReadOnly); }
                        headphones.Activate();
                        typeof(HeadphonesWindow).GetMethod("Save_Click", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(headphones, [headphones, new RoutedEventArgs()]);
                        Check(dialog.Messages.Count == 1 && dialog.Messages[0].Contains("ヘッドフォン設定を保存しました。"), "A successful explicit headphone save shows its localized completion dialog");
                        headphones.Close(); headphones = null;
                        Check(dialog.Messages.Count == 1, "Automatic settings save on close does not add a dialog");
                    }
                    Check(errors.Count == 0, "No dispatcher exceptions during saves and notification dialogs");
                    SpatialJson.Save(Path.Combine(root, "results.json"), new { passed = checks.Count, checks });
                }
                var task = Tests(); var frame = new DispatcherFrame();
                _ = task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
                if (!task.IsCompleted) Dispatcher.PushFrame(frame);
                task.GetAwaiter().GetResult();
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                headphones?.Close(); child?.Close(); model?.StopPlayback(); model?.Cancel(); model?.DetachLanguage(); model?.Visualization.Dispose();
                main?.Close(); Application.Current?.Shutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidDataException("Save completion tests failed", failure);
    }
}
