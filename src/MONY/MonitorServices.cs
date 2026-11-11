using System.Diagnostics;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;

namespace MONY;

internal sealed record EndpointStatus(int? Port = null, bool Lan = false, string? Error = null)
{
    public bool Running => Port.HasValue;
}

internal sealed record SettingsView(
    long Revision, MonitorOptions Options, bool WebPasswordSet, bool ApiPasswordSet, bool StartWithWindows,
    NetworkAddress[] Addresses, string? WebUrl, string? ApiUrl,
    EndpointStatus Web, EndpointStatus Api, string? LoadError, RemoteTargetView[] RemoteTargets)
{
    public string Version => Program.Version;
}

internal sealed class MonitorServices : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Channel<bool> _updates = Channel.CreateBounded<bool>(1);
    private readonly Endpoint _web = new(false);
    private readonly Endpoint _api = new(true);
    private readonly Task _worker;
    private SettingsSnapshot _desired;
    private readonly object _schedule = new();

    private readonly MonitoringSession _session;
    private readonly object _dashboardGate = new();
    private readonly WindowsPalette _palette = new();
    private (DashboardSnapshot Source, SettingsSnapshot Settings, string Id, bool English, DashboardSnapshot Result)? _dashboard;

    internal SettingsStore Settings { get; }
    internal RemoteMonitors Remotes { get; }
    internal MonitorAlerts Alerts { get; }
    internal bool WebAvailable => _web.Status.Running;
    internal string? LocalWebUrl => _web.Status.Port is int port ? MonitorNetwork.Url("127.0.0.1", port) : null;

    internal MonitorServices(SettingsStore settings, MonitoringSession session)
    {
        Settings = settings;
        _session = session;
        Remotes = new RemoteMonitors(settings, session);
        Alerts = new MonitorAlerts(settings, session, Remotes);
        _desired = settings.Current;
        _worker = Task.Run(() => RunAsync(session));
        Refresh(_desired);
    }

    internal void Refresh(SettingsSnapshot settings)
    {
        lock (_schedule)
        {
            if (settings.Revision < _desired.Revision)
                return;
            Volatile.Write(ref _desired, settings);
            _updates.Writer.TryWrite(true);
        }
    }

    internal SettingsView View()
    {
        var current = Settings.Current;
        var options = current.Options;
        var addresses = MonitorNetwork.Addresses();
        var preferred = addresses.FirstOrDefault(item => item.Ip == options.PreferredIp)?.Ip ?? addresses.FirstOrDefault()?.Ip;
        var web = _web.Status;
        var api = _api.Status;
        string Url(bool lan, int port, EndpointStatus status, string path = "/") =>
            MonitorNetwork.Url(lan && (status.Lan || status.Error is null) ? preferred ?? "127.0.0.1" : "127.0.0.1",
                status.Error is not null && status.Port is int actual ? actual : port, path);
        return new SettingsView(current.Revision, options, current.WebPassword is not null, current.ApiPassword is not null, StartupRegistration.Enabled,
            addresses,
            options.WebEnabled ? Url(options.WebLan, options.WebPort, web) : null,
            options.ApiEnabled ? Url(options.ApiLan, options.ApiPort, api, "/api/data") : null,
            web, api, Settings.LoadError, RemoteMonitors.Views(current));
    }

    internal DashboardSnapshot Dashboard(bool localOnly = false, bool english = false)
    {
        var (id, sample, settings) = localOnly
            ? (RemoteMonitors.Local, _session.Snapshot, Settings.Current) : Remotes.Current;
        var options = settings.Options;
        var accent = _palette.Current;
        var localLabel = RemoteMonitors.Label(options.ComputerName, options.PreferredIp);
        if (localLabel.Length == 0) localLabel = UiText.Translate("此电脑", english);

        lock (_dashboardGate)
        {
            if (!localOnly && _dashboard is { } cached && ReferenceEquals(cached.Source, sample) &&
                ReferenceEquals(cached.Settings, settings) && cached.Id == id && cached.English == english && cached.Result.Accent == accent)
                return cached.Result;

            var dashboard = sample.ForDisplay(options.ShowSharedGpuMemory) with
            {
                ComputerName = id == RemoteMonitors.Local ? options.ComputerName : sample.ComputerName,
                ComputerIp = id == RemoteMonitors.Local ? options.PreferredIp : sample.ComputerIp,
                WebTheme = options.WebTheme,
                Language = options.Language,
                Accent = accent,
                TargetId = id,
                Targets = localOnly ? [] : [new(RemoteMonitors.Local, localLabel),
                    .. settings.RemoteTargets.Select(target => new TargetChoice(target.Id,
                        target.Id == id ? RemoteMonitors.Label(sample.ComputerName, sample.ComputerIp) :
                            RemoteMonitors.Label(target.Name, target.Ip)))]
            };
            dashboard = UiText.Dashboard(dashboard, english);
            if (!localOnly) _dashboard = (sample, settings, id, english, dashboard);
            return dashboard;
        }
    }

    private async Task RunAsync(MonitoringSession session)
    {
        try
        {
            await foreach (var _ in _updates.Reader.ReadAllAsync(_shutdown.Token))
            {
                _shutdown.Token.ThrowIfCancellationRequested();
                var options = Volatile.Read(ref _desired).Options;
                var oldWeb = _web.Status;
                var oldApi = _api.Status;
                var webChange = NeedsChange(oldWeb, options.WebEnabled, options.WebLan, options.WebPort);
                var apiChange = NeedsChange(oldApi, options.ApiEnabled, options.ApiLan, options.ApiPort);
                await Task.WhenAll(webChange ? _web.StopAsync() : Task.CompletedTask, apiChange ? _api.StopAsync() : Task.CompletedTask);
                await Task.WhenAll(
                    webChange && options.WebEnabled ? _web.StartAsync(this, session, options.WebLan,
                        options.WebPort, options.ApiPort, oldWeb, _shutdown.Token) : Task.CompletedTask,
                    apiChange && options.ApiEnabled ? _api.StartAsync(this, session, options.ApiLan,
                        options.ApiPort, options.WebPort, oldApi, _shutdown.Token) : Task.CompletedTask);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            await Task.WhenAll(_web.StopAsync(), _api.StopAsync());
        }
    }

    private static bool NeedsChange(EndpointStatus status, bool enabled, bool lan, int port) =>
        enabled ? status.Port != port || status.Lan != lan || status.Error is not null
            : status.Running || status.Error is not null;

    public async ValueTask DisposeAsync()
    {
        _updates.Writer.TryComplete();
        _shutdown.Cancel();
        await Task.WhenAll(_worker, Alerts.DisposeAsync().AsTask(), Remotes.DisposeAsync().AsTask()).ConfigureAwait(false);
        _palette.Dispose();
        _shutdown.Dispose();
    }

    private sealed class Endpoint(bool apiOnly)
    {
        private WebApplication? _application;
        private EndpointStatus _status = new();
        internal EndpointStatus Status => Volatile.Read(ref _status);

        internal async Task StartAsync(MonitorServices services, MonitoringSession session, bool lan, int port,
            int otherPort, EndpointStatus previous, CancellationToken token)
        {
            if (await TryListenAsync(services, session, lan, port, token))
                return;

            if (lan && await TryListenAsync(services, session, false, port, token))
            {
                Volatile.Write(ref _status, new(port, false, "局域网监听启动失败。本机访问可用。"));
                return;
            }

            if (previous.Port is int fallback && fallback != port && fallback != otherPort &&
                await TryListenAsync(services, session, lan && previous.Lan, fallback, token))
            {
                Volatile.Write(ref _status, new(fallback, lan && previous.Lan, $"无法监听 {port}，仍使用端口 {fallback}。"));
                return;
            }

            Volatile.Write(ref _status, new(Error: $"无法监听 {port} 端口。请检查端口占用后重试。"));
        }

        private async Task<bool> TryListenAsync(MonitorServices services, MonitoringSession session, bool lan,
            int port, CancellationToken token)
        {
            try
            {
                _application = DashboardServer.Create(services, session, apiOnly, lan, port);
                await _application.StartAsync(token);
                Volatile.Write(ref _status, new(port, lan));
                return true;
            }
            catch (Exception error) when (error is IOException or SocketException or AggregateException)
            {
                Debug.WriteLine(error);
                await StopAsync();
                return false;
            }
        }

        internal async Task StopAsync()
        {
            Volatile.Write(ref _status, new());
            if (_application is not { } app)
                return;

            _application = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await app.StopAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
            finally
            {
                await app.DisposeAsync();
            }
        }
    }
}
