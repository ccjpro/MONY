using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MONY;

public sealed partial class TimeGraph : UserControl
{
    private MetricPoint[] _points = [];
    private double _maximum = 100;
    private double _secondaryMaximum = 100;

    public TimeGraph()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Draw();
    }

    internal void UseInstrumentStyle()
    {
        GraphGrid.Visibility = Area.Visibility = Visibility.Collapsed;
        InstrumentBaseline.Visibility = Visibility.Visible;
    }

    internal void SetLabels(TextBlock block, string text, MetricLabel[]? labels)
    {
        if (labels is null)
        {
            block.Text = text;
            return;
        }

        block.Inlines.Clear();
        foreach (var label in labels)
        {
            if (label.Text == "\n")
            {
                block.Inlines.Add(new LineBreak());
                continue;
            }

            var run = new Run { Text = label.Text };
            var trace = label.Series switch { 1 => PrimaryLine, 2 => SecondaryLine, 3 => TertiaryLine, _ => null };
            if (trace is not null)
                BindingOperations.SetBinding(run, Run.ForegroundProperty, new Binding { Source = trace, Path = new PropertyPath("Stroke") });
            block.Inlines.Add(run);
        }
    }

    internal void SetHistory(MetricPoint[] points, double maximum, double? secondaryMaximum)
    {
        _points = points;
        _maximum = maximum;
        _secondaryMaximum = secondaryMaximum ?? maximum;
        Draw();
    }

    private void Draw()
    {
        if (ActualWidth < 2 || ActualHeight < 2)
            return;

        Area.Data = Geometry(static p => p.Primary, _maximum, fill: true);
        PrimaryLine.Data = Geometry(static p => p.Primary, _maximum);
        SecondaryLine.Data = Geometry(static p => p.Secondary, _secondaryMaximum);
        TertiaryLine.Data = Geometry(static p => p.Tertiary, _secondaryMaximum);
    }

    private PathGeometry Geometry(Func<MetricPoint, double?> select, double maximum, bool fill = false)
    {
        var geometry = new PathGeometry();
        var bottom = ActualHeight - 1;
        const int capacity = DashboardSnapshot.HistoryLength;
        var step = (ActualWidth - 2) / (capacity - 1);
        PathFigure? figure = null;
        double lastX = 0;

        for (var index = 0; index < _points.Length; index++)
        {
            if (select(_points[index]) is not double value)
            {
                EndRun();
                continue;
            }

            var x = 1 + (capacity - _points.Length + index) * step;
            var y = bottom - Math.Clamp(value / maximum, 0, 1) * (bottom - 1);
            var point = new Point(x, y);
            if (figure is null)
            {
                figure = new PathFigure { StartPoint = fill ? new Point(x, bottom) : point, IsClosed = fill };
                if (fill)
                    figure.Segments.Add(new LineSegment { Point = point });
                geometry.Figures.Add(figure);
            }
            else
            {
                figure.Segments.Add(new LineSegment { Point = point });
            }
            lastX = x;
        }

        EndRun();
        return geometry;

        void EndRun()
        {
            if (fill && figure is not null)
                figure.Segments.Add(new LineSegment { Point = new Point(lastX, bottom) });
            figure = null;
        }
    }
}
