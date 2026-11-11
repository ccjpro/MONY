using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Input;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace MONY;

public sealed partial class MainWindow : Window
{
    private MetricCard[] _cards = [];
    private readonly MonitoringSession _session;
    private readonly MonitorServices _services;
    private SettingsSnapshot? _appliedSettings;
    private long _lastSettingsRefresh;
    private readonly DispatcherQueueTimer _refresh;
    private DashboardSnapshot? _displayed;
    private int _cardColumns = 1;
    private string? _language;
    private string _settingsMessage = "";
    private AppWindowPresenter? _previousPresenter;
    private bool _fullscreenFromSettings;

    internal MainWindow(MonitoringSession session, MonitorServices services)
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id,
            Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var width = Math.Min((int)(900 * scale), area.Width);
        var height = Math.Min((int)(760 * scale), area.Height);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2, width, height));

        _session = session;
        _services = services;

        _refresh = DispatcherQueue.CreateTimer();
        _refresh.Interval = TimeSpan.FromMilliseconds(250);
        _refresh.Tick += (_, _) =>
        {
            if (!AppWindow.IsVisible)
                return;

            ApplySettings();
            if (SettingsView.Visibility == Visibility.Visible && Environment.TickCount64 - _lastSettingsRefresh >= 1000)
            {
                _lastSettingsRefresh = Environment.TickCount64;
                SettingsPage.RefreshStatus(_services.View());
            }
            DisplaySnapshot();
        };
        Closed += (_, _) => _refresh.Stop();
        ApplySettings();
        DisplaySnapshot();
        _refresh.Start();
        AppWindow.Changed += (_, _) => UpdateTitleBarRegions();
        AppTitleBar.Loaded += (_, _) =>
        {
            UpdateTitleBarRegions();
            AppTitleBar.XamlRoot.Changed += (_, _) => UpdateTitleBarRegions();
        };
    }

    internal void ShowDashboard()
    {
        ApplySettings();
        DisplaySnapshot();
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
            { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }
        AppWindow.Show();
    }

    private void OpenSettings(object sender, RoutedEventArgs args)
    {
        SettingsPage.Open(_services, _session, message =>
        {
            _settingsMessage = message;
            SettingsMessage.Text = UiText.T(message);
        });
        DashboardScroll.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
    }

    private void CloseSettings(object sender, RoutedEventArgs args)
    {
        SettingsView.Visibility = Visibility.Collapsed;
        DashboardScroll.Visibility = Visibility.Visible;
        DisplaySnapshot();
    }

    private void RestoreOnEscape(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.Escape ||
            AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Maximized } presenter)
            return;
        args.Handled = true;
        presenter.Restore();
    }

    private void TitleBarSizeChanged(object sender, SizeChangedEventArgs args) => UpdateTitleBarRegions();

    private void UpdateTitleBarRegions()
    {
        if (TitleFullScreenButton.XamlRoot is null || TitleFullScreenButton.ActualWidth < 1 ||
            TitleFullScreenButton.ActualHeight < 1 || AppTitleBar.Visibility != Visibility.Visible) return;
        var scale = TitleFullScreenButton.XamlRoot.RasterizationScale;
        CaptionInset.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);
        var bounds = TitleFullScreenButton.TransformToVisual(Root).TransformBounds(new Rect(0, 0,
            TitleFullScreenButton.ActualWidth, TitleFullScreenButton.ActualHeight));
        var rect = new RectInt32((int)Math.Round(bounds.X * scale), (int)Math.Round(bounds.Y * scale),
            (int)Math.Round(bounds.Width * scale), (int)Math.Round(bounds.Height * scale));
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, [rect]);
    }

    private void EnterFullScreen(object sender, RoutedEventArgs args)
    {
        if (_previousPresenter is not null) return;
        _fullscreenFromSettings = SettingsView.Visibility == Visibility.Visible;
        SettingsView.Visibility = Visibility.Collapsed;
        _previousPresenter = AppWindow.Presenter;
        FullPage.Display(_services.Dashboard(english: UiText.DesktopEnglish));
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        AppTitleBar.Visibility = DashboardScroll.Visibility = Visibility.Collapsed;
        FullPage.Visibility = Visibility.Visible;
        FullPage.FocusExit();
    }

    private void ExitFullScreen(object sender, RoutedEventArgs args)
    {
        if (_previousPresenter is not { } presenter)
            return;

        AppWindow.SetPresenter(presenter);
        _previousPresenter = null;
        FullPage.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = _fullscreenFromSettings ? Visibility.Visible : Visibility.Collapsed;
        AppTitleBar.Visibility = Visibility.Visible;
        UpdateTitleBarRegions();
        DashboardScroll.Visibility = _fullscreenFromSettings ? Visibility.Collapsed : Visibility.Visible;
        DisplaySnapshot();
        TitleFullScreenButton.Focus(FocusState.Programmatic);
    }

    private void ApplySettings()
    {
        var settings = _services.Settings.Current;
        if (ReferenceEquals(settings, _appliedSettings))
            return;

        _appliedSettings = settings;
        var options = settings.Options;
        if (_language != options.Language)
        {
            _language = options.Language;
            SettingsPage.RefreshLanguage(() => UiText.ApplyDesktop(Root, options.Language));
            SettingsMessage.Text = UiText.T(_settingsMessage);
        }
        Root.RequestedTheme = options.DesktopTheme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    private void ResizeDashboard(object sender, SizeChangedEventArgs args)
    {
        const double cardWidth = 264;
        var width = Math.Min(args.NewSize.Width, Dashboard.MaxWidth) - Dashboard.Padding.Left - Dashboard.Padding.Right;
        var gap = MetricGrid.ColumnSpacing;
        var columns = Math.Max(1, (int)((width + gap) / (cardWidth + gap)));
        if (columns != _cardColumns)
        {
            _cardColumns = columns;
            LayoutCards();
        }
        else
        {
            FitCardRows();
        }
    }

    private void ResizeCardRows(object sender, SizeChangedEventArgs args) => FitCardRows();

    private void FitCardRows()
    {
        var available = DashboardScroll.ActualHeight - Dashboard.Padding.Top - Dashboard.Padding.Bottom -
            Header.ActualHeight - Header.Margin.Bottom - DashboardFooter.ActualHeight - DashboardFooter.Margin.Top;
        var scale = DashboardScroll.XamlRoot?.RasterizationScale ?? 1;
        var height = Math.Max(0, Math.Floor((available - MetricGrid.RowSpacing) / 2 * scale) / scale);
        foreach (var row in MetricGrid.RowDefinitions)
            row.MinHeight = height;
    }

    private void LayoutCards()
    {
        var columns = _cardColumns;
        var count = _cards.Length + 1;
        MetricGrid.ColumnDefinitions.Clear();
        MetricGrid.RowDefinitions.Clear();
        for (var column = 0; column < columns; column++)
            MetricGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var row = 0; row < (count + columns - 1) / columns; row++)
            MetricGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var index = 0; index < count; index++)
        {
            var card = index < _cards.Length ? (FrameworkElement)_cards[index] : Partitions;
            Grid.SetRow(card, index / columns);
            Grid.SetColumn(card, index % columns);
        }
        FitCardRows();
    }

    private void SelectTarget(object? sender, string id)
    {
        try { _services.Remotes.Select(id); }
        catch (ArgumentException) { /* A remote target may have been removed in Web settings. */ }
        DisplaySnapshot();
    }

    private void TogglePause(object sender, RoutedEventArgs args)
    {
        _services.Remotes.SetPaused(!_services.Dashboard(english: UiText.DesktopEnglish).Paused);
        DisplaySnapshot();
    }

    private void DisplaySnapshot()
    {
        var snapshot = _services.Dashboard(english: UiText.DesktopEnglish);
        if (FullPage.Visibility == Visibility.Visible)
        {
            FullPage.Display(snapshot);
            return;
        }

        Header.Display(snapshot);
        if (ReferenceEquals(_displayed, snapshot))
            return;

        if (_displayed is null || !snapshot.Metrics.Select(metric => metric.Id)
                .SequenceEqual(_displayed.Metrics.Select(metric => metric.Id)))
        {
            MetricGrid.Children.Clear();
            _cards = snapshot.Metrics.Select(_ => new MetricCard()).ToArray();
            foreach (var card in _cards)
                MetricGrid.Children.Add(card);
            MetricGrid.Children.Add(Partitions);
            LayoutCards();
        }

        if (_displayed is null || !snapshot.Volumes.SequenceEqual(_displayed.Volumes))
            Partitions.Display(snapshot.Volumes);

        _displayed = snapshot;
        for (var index = 0; index < _cards.Length; index++)
            _cards[index].Update(snapshot.Metrics[index]);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr window);
}
