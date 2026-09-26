using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Disc2Flac;

internal sealed class TabMarkerAdorner : Adorner
{
    private readonly TextBox _textBox;
    private static readonly Brush MarkerBackground = new SolidColorBrush(Color.FromRgb(215, 238, 255));
    private static readonly Brush MarkerForeground = new SolidColorBrush(Color.FromRgb(8, 78, 145));
    private static readonly Pen MarkerBorder = new(new SolidColorBrush(Color.FromRgb(94, 165, 219)), 1);
    private static readonly Brush DarkMarkerBackground = new SolidColorBrush(Color.FromRgb(42, 83, 112));
    private static readonly Brush DarkMarkerForeground = new SolidColorBrush(Color.FromRgb(222, 240, 255));
    private static readonly Pen DarkMarkerBorder = new(new SolidColorBrush(Color.FromRgb(95, 168, 218)), 1);

    public TabMarkerAdorner(TextBox textBox) : base(textBox)
    {
        _textBox = textBox;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var text = _textBox.Text;
        if (text.IndexOf('⇥') < 0) return;

        var viewport = new Rect(_textBox.RenderSize);
        drawingContext.PushClip(new RectangleGeometry(viewport));
        var typeface = new Typeface(_textBox.FontFamily, _textBox.FontStyle,
            _textBox.FontWeight, _textBox.FontStretch);
        var glyph = new FormattedText("⇥", CultureInfo.CurrentUICulture, _textBox.FlowDirection,
            typeface, _textBox.FontSize, ThemeService.IsDark ? DarkMarkerForeground : MarkerForeground,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        for (var index = text.IndexOf('⇥'); index >= 0; index = text.IndexOf('⇥', index + 1))
        {
            var leading = _textBox.GetRectFromCharacterIndex(index, trailingEdge: false);
            var trailing = _textBox.GetRectFromCharacterIndex(index, trailingEdge: true);
            if (leading.IsEmpty || trailing.IsEmpty) continue;
            var left = Math.Min(leading.X, trailing.X);
            var width = Math.Max(Math.Abs(trailing.X - leading.X), glyph.WidthIncludingTrailingWhitespace);
            var bounds = new Rect(left, leading.Top, width, Math.Max(leading.Height, glyph.Height));
            if (!bounds.IntersectsWith(viewport)) continue;
            drawingContext.DrawRoundedRectangle(ThemeService.IsDark ? DarkMarkerBackground : MarkerBackground,
                ThemeService.IsDark ? DarkMarkerBorder : MarkerBorder, bounds, 2, 2);
            drawingContext.DrawText(glyph, new Point(bounds.Left + (bounds.Width - glyph.Width) / 2,
                bounds.Top + (bounds.Height - glyph.Height) / 2));
        }
        drawingContext.Pop();
    }
}
