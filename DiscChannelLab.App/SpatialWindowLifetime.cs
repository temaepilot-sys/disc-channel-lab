using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace Disc2Flac;

public partial class SpatialLabWindow
{
    internal bool IsWindowClosed => closed;
    bool sourceRefreshQueued;
    readonly HashSet<MainInteractionLease> mainInteractions = [];
    static readonly ConditionalWeakTable<UIElement, MainInteractionState> mainInteractionStates = new();
    sealed class MainInteractionState { public readonly HashSet<MainInteractionLease> Owners = []; public bool PreviousEnabled; }
    sealed class MainInteractionLease : IDisposable
    {
        readonly SpatialLabWindow lab; readonly UIElement? target; readonly MainInteractionState? state;
        bool released;
        public MainInteractionLease(SpatialLabWindow lab)
        {
            this.lab = lab; target = lab.Owner?.Content as UIElement;
            if (target is null) return;
            state = mainInteractionStates.GetOrCreateValue(target);
            if (state.Owners.Count == 0) state.PreviousEnabled = target.IsEnabled;
            state.Owners.Add(this); lab.mainInteractions.Add(this); target.IsEnabled = false;
        }
        public void Dispose()
        {
            if (released) return; released = true; lab.mainInteractions.Remove(this);
            if (target is not null && state is not null && state.Owners.Remove(this) && state.Owners.Count == 0) target.IsEnabled = state.PreviousEnabled;
        }
    }
    MainInteractionLease LockMainInteraction() => new(this);
    static void CancelRequest(CancellationTokenSource? request)
    { try { request?.Cancel(); } catch (ObjectDisposedException) { } }
    void CancelPendingSettings()
    {
        var hrtf = hrtfSelection; hrtfSelection = null; var room = roomSelection; roomSelection = null;
        CancelRequest(hrtf); CancelRequest(room);
    }
    void CloseLab()
    {
        if (closed) return;
        closed = true; ready = false; timer.Stop(); comparisonRequest++;
        model.PropertyChanged -= Model_Changed; HeadphoneSession.Current.PropertyChanged -= Headphone_Changed;
        // Release this window's locks independently of workers that may still be returning from native / file I/O.
        // A late completion must not release locks acquired by a replacement window.
        foreach (var interaction in mainInteractions.ToArray()) interaction.Dispose();
        void Cleanup(Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                try { new AppLog().Write("SPATIAL WINDOW CLOSE: " + ex); }
                catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
            }
        }
        Cleanup(() => CancelRequest(operation)); Cleanup(() => CancelRequest(comparisonStop)); Cleanup(CancelPendingSettings);
        Cleanup(() => { if (SceneInput.IsMouseCaptured) SceneInput.ReleaseMouseCapture(); });
        Cleanup(model.ClearAdmSourceSolo); Cleanup(SaveSettings);
    }
    void QueueMainSourceRefresh()
    {
        if (closed || sourceRefreshQueued) return;
        sourceRefreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            sourceRefreshQueued = false; if (closed) return;
            UpdateSource(); if (operation is null) FollowMainSource();
            UpdateLabAvailability(); UpdateComparisonPresentation();
        }));
    }
}
