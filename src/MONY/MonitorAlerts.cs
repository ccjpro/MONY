using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace MONY;

internal sealed record AlertCondition(string Key, string Kind, string Source, string Device, double Value);

internal sealed record MonitorAlert(string Id, DateTimeOffset Time, AlertCondition Condition)
{
    internal string Text(bool english)
    {
        var value = Condition.Value.ToString("0.#", CultureInfo.InvariantCulture);
        var message = Condition.Kind switch
        {
            "cpu" => english ? $"CPU temperature is high ({value} °C)." : $"CPU 温度过高（{value} °C）。",
            "gpu" => english ? $"{Condition.Device} temperature is high ({value} °C)." : $"{Condition.Device} 温度过高（{value} °C）。",
            "disk" => english ? $"{Condition.Device} has {value}% free space." : $"{Condition.Device} 剩余空间不足（{value}%）。",
            _ => english ? "The monitoring target is disconnected." : "监控目标已断线。"
        };
        var source = Condition.Source.Length == 0 ? UiText.Translate("此电脑", english) : Condition.Source;
        return $"{source} · {message}";
    }
}

internal sealed class AlertTracker
{
    private const long CooldownMilliseconds = 5 * 60 * 1000;
    private readonly Dictionary<string, (long Since, bool Sent)> _pending = [];
    private long? _lastAlert;

    internal void ResetConditions() => _pending.Clear();

    internal MonitorAlert[] Observe(IEnumerable<AlertCondition> conditions, long now, int delaySeconds)
    {
        if (_lastAlert is long last && now - last < CooldownMilliseconds)
            return [];

        var current = new HashSet<string>();
        var alerts = new List<MonitorAlert>();
        foreach (var condition in conditions)
        {
            if (!current.Add(condition.Key)) continue;
            if (!_pending.TryGetValue(condition.Key, out var state))
                state = (now, false);

            if (!state.Sent && now - state.Since >= delaySeconds * 1000L)
            {
                alerts.Add(new MonitorAlert(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, condition));
                state.Sent = true;
            }
            _pending[condition.Key] = state;
        }

        if (alerts.Count > 0)
            _lastAlert = now;

        // Only completed alerts carry across a gap in detection.
        foreach (var key in _pending.Keys.Where(key =>
            !current.Contains(key) || (alerts.Count > 0 && !_pending[key].Sent)).ToArray())
            _pending.Remove(key);
        return alerts.ToArray();
    }
}

internal sealed class MonitorAlerts : IAsyncDisposable
{
    private readonly SettingsStore _settings;
    private readonly MonitoringSession _session;
    private readonly RemoteMonitors _remotes;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private readonly Queue<MonitorAlert> _webAlerts = new();
    private readonly object _gate = new();

    internal event Action<MonitorAlert>? WindowsAlert;

    internal MonitorAlerts(SettingsStore settings, MonitoringSession session, RemoteMonitors remotes)
    {
        _settings = settings;
        _session = session;
        _remotes = remotes;
        _worker = Task.Run(RunAsync);
    }

    internal MonitorAlert[] ForBrowser(IPAddress? address)
    {
        var options = _settings.Current.Options;
        if (!options.WebNotifications) return [];
        if (address is not null)
        {
            address = SettingsStore.NormalizeAddress(address);
            if ((options.WebMuteLocal && IPAddress.IsLoopback(address)) ||
                (options.WebMuteIps && SettingsStore.MutedAddresses(options.WebMutedAddresses).Contains(address)))
                return [];
        }

        var since = DateTimeOffset.UtcNow.AddMinutes(-2);
        lock (_gate) return _webAlerts.Where(alert => alert.Time >= since).ToArray();
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var tracker = new AlertTracker();
        (int Cpu, int Gpu, int Disk, int Delay) thresholds = default;
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
            {
                var options = _settings.Current.Options;
                var updated = (options.CpuAlertCelsius, options.GpuAlertCelsius, options.DiskAlertPercent, options.AlertDelaySeconds);
                if (thresholds != updated)
                {
                    thresholds = updated;
                    tracker.ResetConditions();
                }

                if (!options.WebNotifications)
                    lock (_gate) _webAlerts.Clear();
                var conditions = options.WindowsNotifications || options.WebNotifications ? Conditions(options) : [];
                foreach (var alert in tracker.Observe(conditions, Environment.TickCount64, options.AlertDelaySeconds))
                {
                    if (options.WebNotifications)
                    {
                        lock (_gate)
                        {
                            if (_webAlerts.Count >= 64) _webAlerts.Dequeue();
                            _webAlerts.Enqueue(alert);
                        }
                    }

                    if (options.WindowsNotifications)
                    {
                        try { WindowsAlert?.Invoke(alert); }
                        catch (Exception error) { Debug.WriteLine(error); }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private IEnumerable<AlertCondition> Conditions(MonitorOptions options)
    {
        var local = _session.Snapshot;
        if (!local.Paused && local.Sample is { } sample && DateTimeOffset.UtcNow - sample.Time < TimeSpan.FromSeconds(10))
        {
            foreach (var condition in Hardware(RemoteMonitors.Local,
                RemoteMonitors.Label(options.ComputerName, options.PreferredIp), sample, options))
                yield return condition;
        }

        var (id, remote, settings) = _remotes.Current;
        if (id == RemoteMonitors.Local || remote.Paused) yield break;
        var label = RemoteMonitors.Label(remote.ComputerName, remote.ComputerIp);
        if (!remote.HasSample && remote.Status == RemoteMonitors.DisconnectedStatus)
        {
            yield return new AlertCondition(id + "/offline", "remote", label, "", 0);
            yield break;
        }

        // A loopback connection is the same hardware already checked above.
        var connection = settings.RemoteTargets.FirstOrDefault(target => target.Id == id);
        if (remote.Sample is not { } remoteSample || connection is null || new Uri(connection.Address).IsLoopback)
            yield break;
        foreach (var condition in Hardware(id, label, remoteSample, options)) yield return condition;
    }

    private static IEnumerable<AlertCondition> Hardware(string id, string label, PerformanceSample sample, MonitorOptions options)
    {
        if (sample.CpuTemperature is double cpu && cpu >= options.CpuAlertCelsius)
            yield return new AlertCondition(id + "/cpu", "cpu", label, "", cpu);

        foreach (var gpu in sample.Gpus)
            if (gpu.Temperature is double temperature && temperature >= options.GpuAlertCelsius)
                yield return new AlertCondition(id + "/gpu/" + gpu.Id, "gpu", label, gpu.Name, temperature);

        foreach (var volume in sample.Volumes)
        {
            if (volume.TotalBytes <= 0) continue;
            var free = 100.0 * volume.FreeBytes / volume.TotalBytes;
            if (free < options.DiskAlertPercent)
                yield return new AlertCondition(id + "/disk/" + volume.Letter, "disk", label, volume.Letter, free);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try { await _worker.ConfigureAwait(false); }
        finally { _shutdown.Dispose(); }
    }
}
