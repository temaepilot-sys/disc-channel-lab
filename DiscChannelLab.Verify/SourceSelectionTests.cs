using Disc2Flac;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

internal static class SourceSelectionTests
{
    public static void Run(string output)
    {
        var root = Path.Combine(Path.GetFullPath(output), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = new List<string>();
        void Check(bool ok, string label)
        {
            if (!ok) throw new InvalidDataException("FAIL " + label);
            checks.Add(label); Console.WriteLine("PASS " + label);
        }
        foreach (var source in new string?[] { null, "", " ", "\t", Path.Combine(root, "missing") })
            Check(!DiscService.HasSupportedSource(source), "Empty or unavailable source safely reports unsupported");

        var iso = Path.Combine(root, "test.iso");
        using (var file = File.Create(iso))
        {
            file.SetLength(510L * 2048 + 8);
            file.Position = 510L * 2048; file.Write("SACDMTOC"u8);
        }
        Check(DiscService.HasSupportedSource(iso), "Readable SACD ISO remains detectable");
        using (var locked = new FileStream(iso, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(!DiscService.HasSupportedSource(iso), "Locked ISO does not throw during source-list refresh");
        Check(DiscService.HasSupportedSource(iso), "ISO detection recovers after the file lock is released");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                var app = new App(); app.InitializeComponent();
                var dispatcherErrors = new List<Exception>();
                app.DispatcherUnhandledException += (_, e) => { dispatcherErrors.Add(e.Exception); e.Handled = true; };
                window = new MainWindow { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
                // Exercise the real selector without scanning actual discs or starting drive timers.
                var loaded = typeof(MainWindow).GetMethod("Window_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)!;
                window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window, loaded);
                var model = (MainViewModel)window.DataContext;
                model.Sources.Clear(); model.Source = null;
                model.OpenSourceAsync(null).GetAwaiter().GetResult();
                Check(model.Source is null && !model.IsBusy, "No selection is represented explicitly and never passed to disc analysis");
                var first = Path.Combine(root, "missing-one");
                var second = Path.Combine(root, "missing-two");
                model.Sources.Add(first); model.Sources.Add(second);
                window.Show(); Pump();
                Check(window.IsLoaded, "Main window is loaded for actual selection-change events");
                var selector = (ComboBox)window.FindName("SourceCombo");
                selector.SetCurrentValue(Selector.SelectedItemProperty, first); Pump();
                selector.SetCurrentValue(Selector.SelectedItemProperty, null); Pump();
                Check(selector.SelectedItem is null && model.Source is null && dispatcherErrors.Count == 0,
                    "Clearing the selected drive is safe, including queued async event errors");
                selector.SetCurrentValue(Selector.SelectedItemProperty, second); Pump();
                model.Sources.Remove(second); Pump();
                Check(selector.SelectedItem is null && model.Source is null && dispatcherErrors.Count == 0,
                    "Removing the selected source from the list does not crash");
                for (int i = 0; i < 10; i++)
                {
                    model.Sources.Clear(); model.Sources.Add(first); model.Sources.Add(second);
                    selector.SetCurrentValue(Selector.SelectedItemProperty, first); Pump();
                    model.Sources.Clear(); Pump();
                }
                Check(selector.SelectedItem is null && model.Source is null && dispatcherErrors.Count == 0,
                    "Repeated list refreshes and selection resets remain stable");
                if (dispatcherErrors.Count != 0) throw new AggregateException(dispatcherErrors);
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw failure;
        File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { Checks = checks }));
        Console.WriteLine($"{checks.Count} source-selection checks passed. Fixtures: {root}");
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
