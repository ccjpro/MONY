using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Data;

namespace MONY;

public sealed partial class FullDashboard : UserControl
{
    private DashboardSnapshot? _snapshot;
    private DashboardVolume[]? _volumes;
    private (double Width, double Height, int Gpus, int Disks, int Volumes) _layout;
    private double _scale = 1;

    public FullDashboard()
    {
        InitializeComponent();
        MemoryTrend.UseInstrumentStyle();
        NetworkTrend.UseInstrumentStyle();
        for (var i = 0; i < 20; i++)
        {
            MemoryScale.RowDefinitions.Add(new RowDefinition());
            var segment = new Border { Background = NetworkModule.BorderBrush };
            Grid.SetRow(segment, i);
            MemoryScale.Children.Add(segment);
        }
        ActualThemeChanged += (_, _) => RefreshInk();
        Loaded += (_, _) => RefreshInk();
        BodyViewport.SizeChanged += (_, _) => Fit();
        Stage.SizeChanged += (_, _) => FitMeasuredHeight();
    }

    public event RoutedEventHandler? ExitRequested;

    private void RequestExit(object sender, RoutedEventArgs args) => ExitRequested?.Invoke(this, args);
    internal void FocusExit() => Exit.Focus(FocusState.Programmatic);

    private void EscapeFullScreen(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.Escape) return;
        args.Handled = true;
        ExitRequested?.Invoke(this, new RoutedEventArgs());
    }

    private void RefreshInk()
    {
        var load = _snapshot?.Metrics.FirstOrDefault(metric => metric.Id == "memory")?.Points.LastOrDefault().Primary;
        for (var i = 0; i < MemoryScale.Children.Count; i++)
            ((Border)MemoryScale.Children[i]).Background = load.HasValue && 19 - i < Math.Ceiling(load.Value / 5)
                ? GpuCount.Foreground : NetworkModule.BorderBrush;
    }

    internal void Display(DashboardSnapshot snapshot)
    {
        if (ReferenceEquals(_snapshot, snapshot)) return;
        var accentChanged = _snapshot?.Accent != snapshot.Accent;
        _snapshot = snapshot;
        if (accentChanged && snapshot.Accent is { } accent)
            ApplyAccent(accent);
        Version.Text = snapshot.Version;
        Unavailable.Text = snapshot.Status;
        Unavailable.Visibility = !snapshot.HasSample && snapshot.TargetId != RemoteMonitors.Local
            ? Visibility.Visible : Visibility.Collapsed;
        Stage.Opacity = Unavailable.Visibility == Visibility.Visible ? 0.35 : 1;
        var cpu = snapshot.Metrics.FirstOrDefault(metric => metric.Id == "cpu");
        if (cpu is not null) Cpu.Display(cpu, snapshot.CpuFrequencyMHz);
        var memory = snapshot.Metrics.FirstOrDefault(metric => metric.Id == "memory");
        if (memory is not null)
        {
            MemoryValue.Text = memory.Value;
            MemoryDetail.Text = memory.Detail;
            MemoryTrend.SetHistory(memory.Points, memory.Maximum, null);
            RefreshInk();
        }
        var gpus = snapshot.Metrics.Where(metric => metric.Id != "cpu" && metric.Id != "memory" && metric.Id != "network" &&
            !metric.Id.StartsWith("disk-", StringComparison.Ordinal)).ToArray();
        var disks = snapshot.Metrics.Where(metric => metric.Id.StartsWith("disk-", StringComparison.Ordinal)).ToArray();
        DisplayLanes(Gpus, gpus);
        DisplayLanes(Disks, disks);
        GpuCount.Text = UiText.Count(gpus.Length);
        DiskCount.Text = UiText.Count(disks.Length);
        GpuEmpty.Visibility = gpus.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        DiskEmpty.Visibility = disks.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var network = snapshot.Metrics.FirstOrDefault(metric => metric.Id == "network");
        if (network is not null)
        {
            Download.Text = network.ValueLabels![0].Text;
            Upload.Text = network.ValueLabels[2].Text;
            NetworkDetail.Text = network.Detail;
            NetworkTrend.SetHistory(network.Points, network.Maximum, null);
        }
        if (_volumes is null || !_volumes.SequenceEqual(snapshot.Volumes))
        {
            _volumes = snapshot.Volumes;
            Volumes.Children.Clear();
            VolumeCount.Text = UiText.Count(snapshot.Volumes.Length);
            VolumesEmpty.Visibility = snapshot.Volumes.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var volume in snapshot.Volumes)
            {
                var row = new StackPanel { Spacing = 6 };
                var title = new Grid { ColumnSpacing = 14 };
                title.ColumnDefinitions.Add(new ColumnDefinition());
                title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var name = new TextBlock { Text = $"{volume.Letter} · {volume.Name}", FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
                ToolTipService.SetToolTip(name, name.Text);
                title.Children.Add(name);
                var capacity = new TextBlock { Text = volume.TotalCapacity, FontSize = 12, TextAlignment = TextAlignment.Right };
                capacity.SetBinding(TextBlock.ForegroundProperty, new Binding { Source = MemoryDetail, Path = new PropertyPath("Foreground") });
                Grid.SetColumn(capacity, 1);
                title.Children.Add(capacity);
                row.Children.Add(title);
                var progress = new ProgressBar { Minimum = 0, Maximum = 100, Value = volume.UsedPercent, Height = 3 };
                progress.SetBinding(Control.ForegroundProperty, new Binding { Source = GpuCount, Path = new PropertyPath("Foreground") });
                progress.SetBinding(Control.BackgroundProperty, new Binding { Source = NetworkModule, Path = new PropertyPath("BorderBrush") });
                AutomationProperties.SetName(progress, volume.Letter + " " + UiText.T("已用容量"));
                row.Children.Add(progress);
                var usage = new TextBlock { Text = volume.Usage, FontSize = 12, TextWrapping = TextWrapping.Wrap };
                usage.SetBinding(TextBlock.ForegroundProperty, new Binding { Source = MemoryDetail, Path = new PropertyPath("Foreground") });
                row.Children.Add(usage);
                Volumes.Children.Add(row);
            }
        }
        Fit();
    }

    private void ApplyAccent(DashboardAccent accent)
    {
        BackdropAccent.SetAccent(accent);
        foreach (var (theme, hex) in new[] { ("Default", accent.Light), ("Light", accent.Dark) })
        {
            if (Resources.ThemeDictionaries[theme] is not ResourceDictionary resources) continue;
            var color = DashboardAccent.ColorFrom(hex);
            foreach (var key in new[] { "AccentFillColorDefaultBrush", "InstrumentAccent" })
                if (resources[key] is Microsoft.UI.Xaml.Media.SolidColorBrush brush) brush.Color = color;
        }
    }

    private static void DisplayLanes(Grid panel, DashboardMetric[] metrics)
    {
        if (panel.Children.Count != metrics.Length || metrics.Where((metric, index) =>
            !Equals(((FrameworkElement)panel.Children[index]).Tag, metric.Id)).Any())
        {
            panel.Children.Clear();
            foreach (var metric in metrics) panel.Children.Add(new InstrumentLane { Tag = metric.Id });
        }
        for (var i = 0; i < metrics.Length; i++) ((InstrumentLane)panel.Children[i]).Display(metrics[i]);
    }

    private static void ArrangeItems(Grid panel, int columns)
    {
        var rows = (panel.Children.Count + columns - 1) / columns;
        if (panel.ColumnDefinitions.Count != columns || panel.RowDefinitions.Count != rows)
        {
            panel.ColumnDefinitions.Clear();
            panel.RowDefinitions.Clear();
            for (var i = 0; i < columns; i++) panel.ColumnDefinitions.Add(new ColumnDefinition());
            for (var i = 0; i < rows; i++) panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        for (var i = 0; i < panel.Children.Count; i++)
        {
            Grid.SetColumn((FrameworkElement)panel.Children[i], i % columns);
            Grid.SetRow((FrameworkElement)panel.Children[i], i / columns);
        }
    }

    private void Fit()
    {
        var width = BodyViewport.ActualWidth;
        var height = BodyViewport.ActualHeight;
        if (width < 1 || height < 1) return;
        var portrait = width / height < 0.88;
        ArrangeItems(Gpus, portrait ? 2 : 1);
        ArrangeItems(Disks, portrait ? 2 : 1);
        ArrangeItems(Volumes, portrait ? 2 : 1);
        var layout = (width, height, Gpus.Children.Count, Disks.Children.Count, Volumes.Children.Count);
        if (_layout == layout) return;
        _layout = layout;
        _scale = Math.Min(1, Math.Min(width / (portrait ? 560 : 900), height / (portrait ? 900 : 680)));
        ApplyStageSize();
        PrimaryDeck.ColumnDefinitions.Clear();
        PrimaryDeck.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(portrait ? 2 : 5, GridUnitType.Star) });
        PrimaryDeck.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(portrait ? 1 : 2.5, GridUnitType.Star) });
        if (!portrait) PrimaryDeck.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) });
        PrimaryDeck.RowDefinitions.Clear();
        PrimaryDeck.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        if (portrait) PrimaryDeck.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(GpuModule, portrait ? 1 : 0);
        Grid.SetColumn(GpuModule, portrait ? 0 : 2);
        Grid.SetColumnSpan(GpuModule, portrait ? 2 : 1);
        GpuModule.BorderThickness = new Thickness(portrait ? 0 : 1, portrait ? 1 : 0, 0, 0);
        GpuModule.Padding = portrait ? new Thickness(0, 18, 0, 0) : new Thickness(26, 0, 0, 0);
        SystemsDeck.ColumnDefinitions.Clear();
        SystemsDeck.RowDefinitions.Clear();
        for (var i = 0; i < (portrait ? 1 : 3); i++) SystemsDeck.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < (portrait ? 3 : 1); i++) SystemsDeck.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(NetworkModule, portrait ? 1 : 0);
        Grid.SetColumn(NetworkModule, portrait ? 0 : 1);
        Grid.SetRow(VolumeModule, portrait ? 2 : 0);
        Grid.SetColumn(VolumeModule, portrait ? 0 : 2);
        NetworkModule.BorderThickness = VolumeModule.BorderThickness = new Thickness(portrait ? 0 : 1, portrait ? 1 : 0, 0, 0);
        NetworkModule.Padding = VolumeModule.Padding = portrait ? new Thickness(0, 16, 0, 0) : new Thickness(26, 0, 0, 0);
    }

    private void ApplyStageSize()
    {
        Stage.Width = _layout.Width / _scale;
        Stage.MinHeight = _layout.Height / _scale;
        Stage.RenderTransform = new Microsoft.UI.Xaml.Media.ScaleTransform { ScaleX = _scale, ScaleY = _scale };
    }

    private void FitMeasuredHeight()
    {
        if (_layout.Height <= 0 || Stage.ActualHeight * _scale <= _layout.Height + 1) return;
        _scale = _layout.Height / Stage.ActualHeight;
        ApplyStageSize();
    }
}
