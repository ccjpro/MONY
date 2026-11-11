using System.Diagnostics;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace MONY;

public sealed partial class App : Application
{
    private MainWindow? _window;
    private MonitoringSession? _session;
    private TrayIcon? _tray;
    private MonitorServices? _services;
    private bool _exiting;
    private bool _notificationRegistrationAttempted;
    private AppNotificationManager? _notifications;

    private readonly bool _startup;

    internal App(bool startup)
    {
        _startup = startup;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _session = new MonitoringSession();
        var settings = new SettingsStore();
        _services = new MonitorServices(settings, _session);
        _window = new MainWindow(_session, _services);
        _tray = new TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(_window),
            ShowDashboard, () => _services.WebAvailable, OpenWeb, ExitApp, TryMinimizeToTray);
        _services.Alerts.WindowsAlert += QueueNotification;

        _window.AppWindow.Closing += (window, eventArgs) =>
        {
            if (_exiting)
                return;

            eventArgs.Cancel = true;
            if (settings.Current.Options.CloseTo == "exit")
                ExitApp();
            else if (_tray.IsRegistered)
                window.Hide();
            else if (window.Presenter is OverlappedPresenter presenter)
                presenter.Minimize();
        };
        _window.AppWindow.Changed += (window, _) =>
        {
            if (window.IsVisible && window.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
                TryMinimizeToTray();
        };

        if (!_startup || !_tray.IsRegistered)
            _window.Activate();
    }

    private void QueueNotification(MonitorAlert alert)
    {
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            if (_exiting || _services?.Settings.Current.Options.WindowsNotifications != true) return;
            var message = alert.Text(UiText.DesktopEnglish);
            try
            {
                if (!_notificationRegistrationAttempted)
                {
                    _notificationRegistrationAttempted = true;
                    if (AppNotificationManager.IsSupported())
                    {
                        var manager = AppNotificationManager.Default;
                        manager.NotificationInvoked += NotificationInvoked;
                        manager.Register();
                        _notifications = manager;
                    }
                }
                if (_notifications is { } notifications)
                {
                    notifications.Show(new AppNotificationBuilder().AddText("MONY").AddText(message).BuildNotification());
                    return;
                }
            }
            catch (Exception error) { Debug.WriteLine(error); }
            _tray?.Notify("MONY", message);
        });
    }

    private void NotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) =>
        _window?.DispatcherQueue.TryEnqueue(ShowDashboard);

    internal void ShowDashboard()
    {
        if (!_exiting)
            _window?.ShowDashboard();
    }

    private bool TryMinimizeToTray()
    {
        if (_exiting || _tray?.IsRegistered != true || _services?.Settings.Current.Options.MinimizeTo != "tray")
            return false;

        return _window?.DispatcherQueue.TryEnqueue(() =>
        {
            if (!_exiting && _services.Settings.Current.Options.MinimizeTo == "tray")
                _window.AppWindow.Hide();
        }) == true;
    }

    private void OpenWeb()
    {
        if (_services?.LocalWebUrl is { } url && !_exiting)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async void ExitApp()
    {
        if (_exiting)
            return;

        _exiting = true;
        if (_services is { } services) services.Alerts.WindowsAlert -= QueueNotification;
        if (_notifications is { } notifications)
        {
            try
            {
                notifications.NotificationInvoked -= NotificationInvoked;
                notifications.Unregister();
            }
            catch (Exception error) { Debug.WriteLine(error); }
        }
        _tray?.Dispose();
        try
        {
            if (_services is not null)
                await _services.DisposeAsync();
        }
        finally
        {
            try
            {
                if (_session is not null)
                    await _session.DisposeAsync();
            }
            finally
            {
                _window?.Close();
            }
        }
    }
}
