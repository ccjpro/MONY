using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MONY;

public sealed partial class MetricCard : UserControl
{
    public MetricCard() => InitializeComponent();

    internal void Update(DashboardMetric metric)
    {
        CardTitle.Text = metric.Title;
        CardIcon.Glyph = metric.Glyph;
        Chart.SetLabels(CardValue, metric.Value, metric.ValueLabels);
        CardDetail.Text = metric.Detail;
        Chart.SetLabels(CardTelemetry, metric.Telemetry ?? string.Empty, metric.TelemetryLabels);
        CardTelemetry.Visibility = metric.Telemetry is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(CardValue, $"{metric.Title} {metric.Value}");
        ToolTipService.SetToolTip(CardDetail, metric.Detail);
        Chart.SetHistory(metric.Points, metric.Maximum, metric.SecondaryMaximum);
    }
}
