using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Disc2Flac;

public sealed class ObjectAudioGuideWindow : Window
{
    public ObjectAudioGuideWindow()
    {
        bool japanese = LanguageService.Instance.IsJapanese;
        Title = japanese ? "オブジェクト音声・ADM ガイド" : "Object audio / ADM guide";
        Width = 760; Height = 680; MinWidth = 520; MinHeight = 400; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("AppBackgroundBrush");
        var document = new FlowDocument { FontFamily = new("Segoe UI"), FontSize = 14, PagePadding = new Thickness(24),
            Background = Background, Foreground = (Brush)Application.Current.FindResource("TextBrush") };
        string path = Path.Combine(AppContext.BaseDirectory, "Guides", japanese ? "object-audio.ja.md" : "object-audio.md");
        var lines = File.Exists(path) ? File.ReadLines(path) : new[] { "# Object audio / ADM",
            "Enable object playback, then use Play and the timeline. Unprepared seeks decode the preceding audio first.",
            "Objects and 5.1 use the selected HRTF. Stereo uses the decoded stereo presentation without HRTF.",
            "Save ADM creates a permanent full-title file. Solo is preview only. No copy protection removal is provided." };
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            int level = line.TakeWhile(c => c == '#').Count();
            var paragraph = new Paragraph(new Run(line.TrimStart('#', ' ').Replace("**", "").Replace("`", ""))) { Margin = new Thickness(0, 0, 0, 10) };
            if (level > 0) { paragraph.FontSize = level == 1 ? 25 : 18; paragraph.FontWeight = FontWeights.SemiBold; paragraph.Margin = new(0, 16, 0, 10); }
            document.Blocks.Add(paragraph);
        }
        Content = new FlowDocumentScrollViewer { Document = document, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsToolBarVisible = false };
    }
}
