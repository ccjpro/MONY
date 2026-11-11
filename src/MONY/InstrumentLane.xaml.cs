using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MONY;

public sealed partial class InstrumentLane : UserControl
{
    private double? _load;

    public InstrumentLane()
    {
        InitializeComponent();
        Trend.UseInstrumentStyle();
        for (var i = 0; i < 20; i++)
        {
            Ruler.ColumnDefinitions.Add(new ColumnDefinition());
            var segment = new Border { Background = Ruler.BorderBrush };
            Grid.SetColumn(segment, i);
            Ruler.Children.Add(segment);
        }
        Loaded += (_, _) => RefreshInk();
        ActualThemeChanged += (_, _) => RefreshInk();
    }

    private void RefreshInk()
    {
        for (var i = 0; i < Ruler.Children.Count; i++)
            ((Border)Ruler.Children[i]).Background = _load.HasValue && i < Math.Ceiling(_load.Value / 5)
                ? Title.Foreground : Ruler.BorderBrush;
    }

    internal void Display(DashboardMetric metric)
    {
        var disk = metric.Id.StartsWith("disk-", StringComparison.Ordinal);
        Title.Text = metric.Title;
        Trend.SetLabels(Value, metric.Value, metric.ValueLabels);
        Title.FontSize = Value.FontSize = disk ? 24 : 32;
        Detail.Text = metric.Detail;
        Trend.SetLabels(Telemetry, metric.Telemetry ?? "", metric.TelemetryLabels);
        AutomationProperties.SetName(Value, metric.Title + " " + metric.Value);
        Ruler.Visibility = disk ? Visibility.Collapsed : Visibility.Visible;
        Trend.Height = disk ? 24 : 42;
        Trend.SetHistory(metric.Points, metric.Maximum, metric.SecondaryMaximum);
        _load = metric.Points.LastOrDefault().Primary;
        RefreshInk();
    }
}
