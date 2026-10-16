using System.Runtime.InteropServices;

namespace Disc2Flac;

/// <summary>Keep WASAPI, DSP and MMCSS registration on one thread, independent of async decoder continuations.</summary>
public static class HeadphoneAudioThread
{
    public static Task Run(Action render)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { render(); done.TrySetResult(); }
            catch (OperationCanceledException ex) { done.TrySetCanceled(ex.CancellationToken); }
            catch (Exception ex) { done.TrySetException(ex); }
        }) { IsBackground = true, Name = "DiscChannelLab headphone audio", Priority = ThreadPriority.AboveNormal };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return done.Task;
    }

    public sealed class Scheduling : IDisposable
    {
        private IntPtr _handle;
        public bool IsRegistered => _handle != IntPtr.Zero;
        public Scheduling()
        {
            uint task = 0;
            _handle = AvSetMmThreadCharacteristicsW("Pro Audio", ref task);
        }
        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            AvRevertMmThreadCharacteristics(_handle);
            _handle = IntPtr.Zero;
        }
        [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);
        [DllImport("avrt.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
    }
}
