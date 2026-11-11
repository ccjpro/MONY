using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MONY;

public sealed partial class PartitionCard : UserControl
{
    private DashboardVolume[]? _volumes;
    private int _columns;
    private readonly List<(TextBlock Name, TextBlock Capacity, ProgressBar Progress, TextBlock Usage)> _rows = [];

    public PartitionCard()
    {
        InitializeComponent();
        Rows.SizeChanged += (_, _) => ArrangeRows();
    }

    private void ArrangeRows(bool force = false)
    {
        var columns = Math.Max(1, (int)((Rows.ActualWidth + 10) / 232));
        if (!force && columns == _columns)
            return;

        _columns = columns;
        Rows.ColumnDefinitions.Clear();
        Rows.RowDefinitions.Clear();
        for (var i = 0; i < columns; i++)
            Rows.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < Rows.Children.Count; i++)
        {
            if (i % columns == 0)
                Rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow((FrameworkElement)Rows.Children[i], i / columns);
            Grid.SetColumn((FrameworkElement)Rows.Children[i], i % columns);
        }
    }

    internal void Display(DashboardVolume[] volumes)
    {
        if (_volumes is not null && _volumes.SequenceEqual(volumes))
            return;

        _volumes = volumes;
        Count.Text = UiText.Count(volumes.Length);
        Empty.Visibility = volumes.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_rows.Count != volumes.Length)
        {
            _rows.Clear();
            Rows.Children.Clear();
            foreach (var _ in volumes)
            {
                var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13 };
                var capacity = new TextBlock { FontSize = 12, TextAlignment = TextAlignment.Right };
                var heading = new Grid { ColumnSpacing = 8 };
                heading.ColumnDefinitions.Add(new ColumnDefinition());
                heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(capacity, 1);
                heading.Children.Add(name);
                heading.Children.Add(capacity);
                var progress = new ProgressBar { Minimum = 0, Maximum = 100 };
                var row = new StackPanel { Spacing = 4 };
                row.Children.Add(heading);
                row.Children.Add(progress);
                var usage = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
                row.Children.Add(usage);
                Rows.Children.Add(row);
                _rows.Add((name, capacity, progress, usage));
            }
            ArrangeRows(force: true);
        }

        for (var index = 0; index < volumes.Length; index++)
        {
            var volume = volumes[index];
            var row = _rows[index];
            row.Name.Text = $"{volume.Letter} · {volume.Name}";
            ToolTipService.SetToolTip(row.Name, row.Name.Text);
            row.Capacity.Text = volume.TotalCapacity;
            row.Progress.Value = volume.UsedPercent;
            row.Usage.Text = volume.Usage;
            AutomationProperties.SetName(row.Progress, row.Name.Text + " " + UiText.T("已用容量"));
        }
    }
}
