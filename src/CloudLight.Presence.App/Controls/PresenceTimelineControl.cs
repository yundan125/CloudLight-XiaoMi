using System.Collections;
using System.Windows;
using System.Windows.Media;
using CloudLight.Presence.Core.Models;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace CloudLight.Presence.App.Controls;

public sealed class PresenceTimelineControl : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(nameof(Segments), typeof(IEnumerable), typeof(PresenceTimelineControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(nameof(From), typeof(DateTimeOffset), typeof(PresenceTimelineControl), new FrameworkPropertyMetadata(default(DateTimeOffset), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ToProperty = DependencyProperty.Register(nameof(To), typeof(DateTimeOffset), typeof(PresenceTimelineControl), new FrameworkPropertyMetadata(default(DateTimeOffset), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DaysProperty = DependencyProperty.Register(nameof(Days), typeof(int), typeof(PresenceTimelineControl), new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public IEnumerable? Segments { get => (IEnumerable?)GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public DateTimeOffset From { get => (DateTimeOffset)GetValue(FromProperty); set => SetValue(FromProperty, value); }
    public DateTimeOffset To { get => (DateTimeOffset)GetValue(ToProperty); set => SetValue(ToProperty, value); }
    public int Days { get => (int)GetValue(DaysProperty); set => SetValue(DaysProperty, value); }
    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize) => new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, Math.Max(70, Math.Min(30, Days) * 28 + 32));
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); if (To <= From || ActualWidth < 100) return;
        var rows = Math.Max(1, Math.Min(30, Days)); var labelWidth = rows == 1 ? 48d : 64d; var barLeft = labelWidth; var barWidth = Math.Max(1, ActualWidth - labelWidth); var rowHeight = 28d;
        var online = new SolidColorBrush(Color.FromRgb(34, 197, 94)); var offline = new SolidColorBrush(Color.FromRgb(220, 228, 239)); var unknown = new DrawingBrush { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute, Drawing = new GeometryDrawing(new SolidColorBrush(Color.FromRgb(241, 245, 249)), null, Geometry.Parse("M0,8 L8,0 M-2,2 L2,-2 M6,10 L10,6")) { Pen = new Pen(new SolidColorBrush(Color.FromRgb(148, 163, 184)), 1) } };
        var typeface = new Typeface("Segoe UI"); var segments = Segments?.Cast<PresenceTimelineSegment>().ToArray() ?? [];
        for (var row = 0; row < rows; row++)
        {
            DateTimeOffset rowStart; DateTimeOffset rowEnd; string label;
            if (rows == 1) { rowStart = From; rowEnd = To; label = "24h前"; }
            else { rowEnd = To.AddDays(-row); rowStart = rowEnd.AddDays(-1); label = rowStart.ToLocalTime().ToString("M/d"); }
            var y = row * rowHeight; dc.DrawText(new FormattedText(label, System.Globalization.CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, typeface, 11, new SolidColorBrush(Color.FromRgb(100, 116, 139)), VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(0, y + 6));
            dc.DrawRoundedRectangle(unknown, null, new Rect(barLeft, y + 4, barWidth, 18), 4, 4);
            foreach (var segment in segments)
            {
                var start = segment.Start > rowStart ? segment.Start : rowStart; var end = segment.End < rowEnd ? segment.End : rowEnd; if (end <= start) continue;
                var x = barLeft + (start - rowStart).TotalSeconds / (rowEnd - rowStart).TotalSeconds * barWidth; var width = (end - start).TotalSeconds / (rowEnd - rowStart).TotalSeconds * barWidth;
                dc.DrawRectangle(segment.State == PresenceState.Online ? online : segment.State == PresenceState.Offline ? offline : unknown, null, new Rect(x, y + 4, Math.Max(1, width), 18));
            }
            if (rows == 1)
            {
                for (var hour = 6; hour < 24; hour += 6)
                {
                    var x = barLeft + barWidth * hour / 24d; dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(148, 163, 184)), .7), new Point(x, y + 3), new Point(x, y + 23));
                    var tick = rowStart.AddHours(hour).ToLocalTime().ToString("HH:mm"); dc.DrawText(new FormattedText(tick, System.Globalization.CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, typeface, 10, new SolidColorBrush(Color.FromRgb(100, 116, 139)), VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x - 13, y + 28));
                }
            }
        }
        dc.DrawText(new FormattedText(rows == 1 ? "现在" : "每天一行", System.Globalization.CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, typeface, 11, new SolidColorBrush(Color.FromRgb(100, 116, 139)), VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(Math.Max(labelWidth, ActualWidth - (rows == 1 ? 28 : 55)), rows == 1 ? 47 : rows * rowHeight + 2));
    }
}
