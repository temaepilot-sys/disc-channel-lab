using System.Windows;

namespace Disc2Flac;

public static class SaveCompletion
{
    // Called by explicit Save / Export commands after successful persistence, never by autosave.
    public static void Show(Window owner, string message, string? destination = null)
    {
        if (!owner.IsLoaded) return;
        static Window Root(Window window) { while (window.Owner is { } parent) window = parent; return window; }
        var root = Root(owner);
        var foreground = Application.Current?.Windows.OfType<Window>()
            .FirstOrDefault(w => w.IsLoaded && w.IsActive && ReferenceEquals(Root(w), root)) ?? owner;
        string text = LanguageService.T(message);
        if (!string.IsNullOrWhiteSpace(destination)) text += "\n\n" + destination;
        MessageBox.Show(foreground, text, "DiscChannelLab", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
