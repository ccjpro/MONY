using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

namespace MONY;

internal sealed record RemoteTarget(string Id, string Address, string Name, string Ip, string? Secret);
internal sealed record RemoteTargetView(string Id, string Address, string Label, bool PasswordSet);
internal sealed record TargetChoice(string Id, string Label);
internal sealed record RemoteConnection(string? Address, string? Password, string? Ip = null, int? Port = null);
internal sealed record TargetSelection(string? Id);
internal sealed record RawMonitorData(string? Version, string? ComputerName, string? ComputerIp, bool Paused,
    PerformanceSample? Sample, string? CpuDetails = null);

internal sealed class RemoteMonitors : IAsyncDisposable
{
    internal const string Local = "local";
    internal const string DisconnectedStatus = "目标电脑连接中断 · 正在重试";
    private readonly SettingsStore _settings;
    private readonly MonitoringSession _session;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HttpClient _client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false
    }) { Timeout = TimeSpan.FromSeconds(4), MaxResponseContentBufferSize = 1024 * 1024 };
    private readonly Queue<PerformanceSample> _history = new(DashboardSnapshot.HistoryLength);
    private readonly Task _polling;
    private string _selected = Local;
    private DashboardSnapshot? _remote;
    private bool _paused;
    private long _generation;

    internal RemoteMonitors(SettingsStore settings, MonitoringSession session)
    {
        _settings = settings;
        _session = session;
        _polling = Task.Run(PollAsync);
    }

    internal (string Id, DashboardSnapshot Snapshot, SettingsSnapshot Settings) Current
    {
        get { lock (_gate) return (_selected, _remote ?? _session.Snapshot, _settings.Current); }
    }

    internal static RemoteTargetView[] Views(SettingsSnapshot settings) => settings.RemoteTargets.Select(target =>
        new RemoteTargetView(target.Id, target.Address, Label(target.Name, target.Ip), target.Secret is not null)).ToArray();

    internal static string Label(string name, string ip) =>
        name.Length == 0 ? ip : ip.Length == 0 ? name : $"{name} · {ip}";

    internal async Task ConnectAsync(RemoteConnection connection, CancellationToken cancellation = default)
    {
        var endpoint = ApiAddress(connection);
        var password = string.IsNullOrEmpty(connection.Password) ? null : connection.Password;
        var data = await ReadAsync(endpoint, password, cancellation).ConfigureAwait(false);
        if (data.Sample is null)
            throw new ArgumentException("已连接 API，等待目标采集首个样本后再试。");

        lock (_gate)
        {
            var current = _settings.Current.RemoteTargets.FirstOrDefault(item => item.Address == endpoint.AbsoluteUri);
            var target = new RemoteTarget(current?.Id ?? Guid.NewGuid().ToString("N"), endpoint.AbsoluteUri,
                Identity(data.ComputerName), Ip(data.ComputerIp, endpoint), password is null ? null : RemoteSecret.Protect(password));
            _settings.SaveRemote(target, remove: false);
            if (_selected == target.Id)
            {
                _generation++;
                _history.Clear();
                _remote = Build(target, data);
                if (_paused) _remote = _remote with { Paused = true, Status = "已暂停" };
            }
        }
    }

    internal void Remove(string id)
    {
        lock (_gate)
        {
            var target = _settings.Current.RemoteTargets.FirstOrDefault(target => target.Id == id)
                ?? throw new ArgumentException("找不到该连接。");
            _settings.SaveRemote(target, remove: true);
            if (_selected == id)
                Select(Local);
        }
    }

    internal void Select(string? id)
    {
        lock (_gate)
        {
            if (id == _selected)
                return;
            var target = id == Local ? null : _settings.Current.RemoteTargets.FirstOrDefault(item => item.Id == id)
                ?? throw new ArgumentException("找不到该监控目标。");
            _generation++;
            _selected = id!;
            _paused = false;
            _history.Clear();
            _remote = target is null ? null : Waiting(target, "正在连接目标电脑");
        }
    }

    internal void SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (_selected == Local)
            {
                _session.SetPaused(paused);
                return;
            }

            if (_paused == paused) return;
            _generation++;
            _paused = paused;
            if (paused)
                _remote = _remote! with { Paused = true, Status = "已暂停" };
            else
            {
                _history.Clear();
                _remote = _remote!.Resume();
            }
        }
    }

    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
            {
                RemoteTarget? target;
                long generation;
                lock (_gate)
                {
                    target = _paused ? null : _settings.Current.RemoteTargets.FirstOrDefault(item => item.Id == _selected);
                    generation = _generation;
                }
                if (target is null) continue;

                try
                {
                    var data = await ReadAsync(new Uri(target.Address),
                        target.Secret is null ? null : RemoteSecret.Unprotect(target.Secret), _shutdown.Token).ConfigureAwait(false);
                    lock (_gate)
                        if (generation == _generation)
                            _remote = Build(target, data);
                }
                catch (Exception error) when (error is ArgumentException or CryptographicException or FormatException)
                {
                    lock (_gate)
                    {
                        if (generation != _generation) continue;
                        _history.Clear();
                        _remote = Waiting(target, DisconnectedStatus);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private DashboardSnapshot Build(RemoteTarget target, RawMonitorData data)
    {
        if (data.Sample is { } sample && (_history.Count == 0 || _history.Last().Time != sample.Time))
        {
            if (_history.Count == DashboardSnapshot.HistoryLength) _history.Dequeue();
            _history.Enqueue(sample);
        }
        var snapshot = DashboardSnapshot.Create(false, _history, Identity(data.CpuDetails, 256));
        return snapshot with
        {
            Status = data.Paused ? "目标电脑已暂停" : snapshot.Status,
            ComputerName = Identity(data.ComputerName),
            ComputerIp = Ip(data.ComputerIp, new Uri(target.Address))
        };
    }

    private static DashboardSnapshot Waiting(RemoteTarget target, string status) =>
        DashboardSnapshot.Create(false, new(), "") with { ComputerName = target.Name, ComputerIp = target.Ip, Status = status };

    private async Task<RawMonitorData> ReadAsync(Uri endpoint, string? password, CancellationToken cancellation)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            if (password is not null)
            {
                var bytes = Encoding.UTF8.GetBytes("pulse:" + password);
                try { request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(bytes)); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }

            using var response = await _client.SendAsync(request, cancellation).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new ArgumentException("API 密码错误或目标要求密码。");
            if (!response.IsSuccessStatusCode)
                throw new ArgumentException($"目标 API 返回 HTTP {(int)response.StatusCode}。");
            var data = await response.Content.ReadFromJsonAsync<RawMonitorData>(cancellationToken: cancellation).ConfigureAwait(false);
            if (data is null || !ValidSample(data.Sample))
                throw new ArgumentException("目标未返回有效的 MONY 数据。");
            if (!Version.TryParse(data.Version, out var version))
                throw new ArgumentException("目标 API 缺少有效的版本号。");
            var comparable = new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
            if (comparable > typeof(Program).Assembly.GetName().Version!)
                throw new ArgumentException("目标 API 版本高于本机，请先升级本机 MONY。");
            return data;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new ArgumentException("连接目标 API 超时。");
        }
        catch (HttpRequestException)
        {
            throw new ArgumentException("无法连接目标 API，请检查地址和 API 服务。");
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or IOException or NotSupportedException)
        {
            throw new ArgumentException("目标未返回有效的 MONY 数据。");
        }
    }

    private static bool ValidSample(PerformanceSample? sample) => sample is null ||
        (sample.Time != default && sample.Gpus is { Length: <= 256 } && sample.Disks is { Length: <= 256 } &&
            sample.Volumes is { Length: <= 256 } &&
            sample.Gpus.All(gpu => gpu is { Id.Length: > 0 and <= 256, Name.Length: <= 512 }) &&
            sample.Disks.All(disk => disk is { Id.Length: > 0 and <= 256, Name.Length: <= 512 }) &&
            sample.Volumes.All(volume => volume is { Letter.Length: > 0 and <= 32, Name.Length: <= 256, TotalBytes: > 0, FreeBytes: >= 0 } &&
                volume.FreeBytes <= volume.TotalBytes));

    private static Uri ApiAddress(RemoteConnection connection)
    {
        if (connection.Ip is not null || connection.Port is not null)
        {
            if (!IPAddress.TryParse(connection.Ip?.Trim(), out var ip) || connection.Port is not (>= 1 and <= 65535))
                throw new ArgumentException("请输入有效的 IP 地址和 1 至 65535 的端口。");
            return new UriBuilder("http", ip.ToString(), connection.Port.Value, "/api/data").Uri;
        }

        var address = connection.Address;
        if (address is null || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath is not ("/" or "/api/data" or "/api/data/"))
            throw new ArgumentException("请输入 HTTP 或 HTTPS 的 MONY API 地址，例如 http://127.0.0.1:2727/api/data。");
        return new UriBuilder(uri) { Path = "/api/data" }.Uri;
    }

    private static string Identity(string? text, int length = 64) =>
        text is null ? "" : new string(text.Where(c => !char.IsControl(c)).Take(length).ToArray()).Trim();

    private static string Ip(string? value, Uri endpoint) =>
        IPAddress.TryParse(value, out var ip) ? ip.ToString() : endpoint.Host;

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try { await _polling.ConfigureAwait(false); }
        finally { _client.Dispose(); _shutdown.Dispose(); }
    }
}
