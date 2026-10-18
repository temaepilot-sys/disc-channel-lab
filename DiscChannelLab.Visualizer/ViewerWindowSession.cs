using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace DiscChannelLab.Visualizer;

/// <summary>Coalesces button requests off the PCM/HTTP threads. Owns process handles, never kills browsers.</summary>
public sealed class ViewerWindowSession(Func<Process> launch, Action<Exception> failed,
    Func<Process, bool>? activate = null) : IDisposable
{
    readonly Channel<bool> requests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    readonly CancellationTokenSource stop = new();
    Process? browser;
    readonly object gate = new();
    Task? running;
    bool disposed;

    public void RequestOpen()
    {
        // Requests are reliable even when the bounded PCM queue is full, and bursts coalesce.
        lock (gate)
        {
            if (disposed) return;
            running ??= Task.Run(RunAsync);
            requests.Writer.TryWrite(true);
        }
    }
    async Task RunAsync()
    {
        try
        {
            await foreach (var _ in requests.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                try
                {
                    if (browser is not null && !browser.HasExited && (activate ?? Activate)(browser)) continue;
                    browser?.Dispose();
                    browser = null;
                    browser = launch();
                    try { browser.WaitForInputIdle(2000); }
                    catch (InvalidOperationException) { } // Chromium may hand off to its existing profile process.
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
                { failed(e); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { browser?.Dispose(); }
    }
    static bool Activate(Process process)
    {
        process.Refresh(); var window = process.MainWindowHandle;
        if (window == 0 || !IsWindow(window)) return false;
        if (IsIconic(window)) ShowWindowAsync(window, 9);
        SetForegroundWindow(window);
        return true;
    }
    [DllImport("user32.dll")] static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint window);
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; requests.Writer.TryComplete(); stop.Cancel();
            if (running is null) stop.Dispose();
            else _ = running.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
        }
    }
}
