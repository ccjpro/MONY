using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace MONY;

public sealed partial class ProcessorInstrument : UserControl
{
    public ProcessorInstrument()
    {
        InitializeComponent();
        Trend.UseInstrumentStyle();
        Track.Points = ArcPoints(1);
        FrequencyTrack.Points = ArcPoints(1, 55, 282);
        for (var i = 0; i <= 20; i++)
        {
            var outer = Position(i / 20.0, 204);
            var inner = Position(i / 20.0, i % 4 == 0 ? 180 : 192);
            Ticks.Children.Add(new Line { X1 = inner.X, Y1 = inner.Y, X2 = outer.X, Y2 = outer.Y,
                Stroke = Track.Stroke, StrokeThickness = i % 4 == 0 ? 2 : 1 });
            if (i % 4 != 0) continue;
            var point = Position(i / 20.0, 157);
            var label = new TextBlock { Text = (i * 5).ToString(), FontSize = 12, Foreground = Detail.Foreground,
                Width = 34, TextAlignment = TextAlignment.Center };
            Canvas.SetLeft(label, point.X - 17);
            Canvas.SetTop(label, point.Y - 8);
            Ticks.Children.Add(label);
        }
        Loaded += (_, _) => RefreshInk();
        ActualThemeChanged += (_, _) => RefreshInk();
    }

    private void RefreshInk()
    {
        foreach (var line in Ticks.Children.OfType<Line>()) line.Stroke = Track.Stroke;
        foreach (var text in Ticks.Children.OfType<TextBlock>()) text.Foreground = Detail.Foreground;
    }

    internal void Display(DashboardMetric metric, double? frequencyMHz)
    {
        Value.Text = metric.Value.Replace("%", "");
        AutomationProperties.SetName(Value, UiText.T("处理器使用率") + " " + metric.Value);
        var frequency = frequencyMHz is > 0 ? frequencyMHz.Value / 1000 : (double?)null;
        FrequencyValue.Text = frequency is double ghz ? $"{ghz:F2} GHz" : "—";
        FrequencyArc.Visibility = frequency.HasValue ? Visibility.Visible : Visibility.Collapsed;
        var scale = Math.Max(6, Math.Ceiling(frequency ?? 0));
        FrequencyArc.Points = ArcPoints((frequency ?? 0) / scale, 55, 282);
        AutomationProperties.SetName(FrequencyValue, UiText.T("频率") + " " + FrequencyValue.Text);
        Detail.Text = metric.Detail;
        var readings = (metric.Telemetry ?? "").Split(" · ");
        if (Readings.Children.Count != readings.Length)
        {
            Readings.Children.Clear();
            Readings.ColumnDefinitions.Clear();
            for (var i = 0; i < readings.Length; i++)
            {
                Readings.ColumnDefinitions.Add(new ColumnDefinition());
                var text = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(text, i);
                Readings.Children.Add(text);
            }
        }
        for (var i = 0; i < readings.Length; i++) ((TextBlock)Readings.Children[i]).Text = readings[i];
        var value = metric.Points.LastOrDefault().Primary;
        Unit.Visibility = Cursor.Visibility = Arc.Visibility = value.HasValue ? Visibility.Visible : Visibility.Collapsed;
        var fraction = Math.Clamp((value ?? 0) / 100, 0, 1);
        Arc.Points = ArcPoints(fraction);
        var start = Position(fraction, 175);
        var end = Position(fraction, 214);
        Cursor.X1 = start.X; Cursor.Y1 = start.Y;
        Cursor.X2 = end.X; Cursor.Y2 = end.Y;
        Trend.SetHistory(metric.Points, metric.Maximum, null);
    }

    private static Point Position(double fraction, double radius, double centerY = 220)
    {
        var angle = (150 + fraction * 240) * Math.PI / 180;
        return new Point(240 + Math.Cos(angle) * radius, centerY + Math.Sin(angle) * radius);
    }

    private static PointCollection ArcPoints(double fraction, double radius = 190, double centerY = 220)
    {
        var points = new PointCollection();
        for (var i = 0; i <= 80; i++) points.Add(Position(fraction * i / 80, radius, centerY));
        return points;
    }
}
