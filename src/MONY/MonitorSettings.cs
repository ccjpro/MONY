using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;

namespace MONY;

internal sealed record MonitorOptions
{
    public string ComputerName { get; init; } = "";
    public string PreferredIp { get; init; } = "";
    public string DesktopTheme { get; init; } = "system";
    public string WebTheme { get; init; } = "system";
    public string Language { get; init; } = "system";
    public string MinimizeTo { get; init; } = "taskbar";
    public string CloseTo { get; init; } = "tray";
    public bool ShowSharedGpuMemory { get; init; } = true;
    public bool WebEnabled { get; init; } = true;
    public bool WebLan { get; init; }
    public int WebPort { get; init; } = DashboardServer.DefaultWebPort;
    public bool ApiEnabled { get; init; } = true;
    public bool ApiLan { get; init; }
    public int ApiPort { get; init; } = DashboardServer.DefaultApiPort;
    public bool WindowsNotifications { get; init; }
    public bool WebNotifications { get; init; }
    public bool WebMuteLocal { get; init; }
    public bool WebMuteIps { get; init; }
    public string WebMutedAddresses { get; init; } = "";
    public int CpuAlertCelsius { get; init; } = 90;
    public int GpuAlertCelsius { get; init; } = 85;
    public int DiskAlertPercent { get; init; } = 10;
    public int AlertDelaySeconds { get; init; } = 15;
}

internal sealed record PasswordCredential(byte[] Salt, byte[] Hash)
{
    private const int Iterations = 600_000;
    private byte[]? _lastVerified;
    private readonly object _gate = new();
    private long _attemptWindow;
    private int _attempts;

    internal static PasswordCredential Create(ReadOnlySpan<byte> password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return new PasswordCredential(salt, Derive(password, salt));
    }

    internal bool Verify(ReadOnlySpan<byte> password)
    {
        var token = SHA256.HashData(password);
        var previous = Volatile.Read(ref _lastVerified);
        if (previous is not null && CryptographicOperations.FixedTimeEquals(token, previous))
            return true;

        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (now - _attemptWindow >= 10_000)
            {
                _attemptWindow = now;
                _attempts = 0;
            }
            if (_attempts >= 10)
                return false;

            _attempts++;
            if (!CryptographicOperations.FixedTimeEquals(Derive(password, Salt), Hash))
                return false;

            _attempts = 0;
            Volatile.Write(ref _lastVerified, token);
            return true;
        }
    }

    private static byte[] Derive(ReadOnlySpan<byte> password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
}

internal sealed record SettingsSnapshot(
    long Revision, MonitorOptions Options, PasswordCredential? WebPassword = null, PasswordCredential? ApiPassword = null)
{
    public RemoteTarget[] RemoteTargets { get; init; } = [];
}

internal sealed record SettingsChange(long Revision, MonitorOptions? Options, string? WebPassword, string? ApiPassword);
internal sealed record SettingUpdate(string? Name, JsonElement Value);

internal sealed class SettingsConflictException : Exception { }

internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MONY", "settings.json");
    private readonly object _gate = new();
    private SettingsSnapshot _current;

    internal SettingsStore()
    {
        try
        {
            _current = File.Exists(_path)
                ? JsonSerializer.Deserialize<SettingsSnapshot>(File.ReadAllBytes(_path), Json) ?? new(0, new())
                : new(0, new());
            Validate(_current.Options);
            if (_current.RemoteTargets is null || _current.RemoteTargets.Any(target =>
                target is null || !Guid.TryParseExact(target.Id, "N", out _) ||
                !Uri.TryCreate(target.Address, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
                uri.Fragment.Length != 0 || uri.AbsolutePath != "/api/data" || target.Name is null || target.Ip is null))
                throw new ArgumentException("保存的监控连接无效。");
            foreach (var password in new[] { _current.WebPassword, _current.ApiPassword })
            {
                if (password is not null && (password.Salt is not { Length: 16 } || password.Hash is not { Length: 32 }))
                    throw new ArgumentException("保存的密码校验数据无效。");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            LoadError = "无法读取已保存的设置。当前使用默认设置，保存后会更新配置文件。";
            _current = new(0, new());
        }
    }

    internal SettingsSnapshot Current => Volatile.Read(ref _current);
    internal string? LoadError { get; private set; }

    internal SettingsSnapshot Save(SettingsChange change)
    {
        lock (_gate)
        {
            var current = _current;
            if (current.Revision != change.Revision)
                throw new SettingsConflictException();

            return SaveCore(current, change.Options, change.WebPassword, change.ApiPassword);
        }
    }

    internal SettingsSnapshot Update(SettingUpdate change)
    {
        lock (_gate)
        {
            var current = _current;
            if (change.Name == "startWithWindows")
            {
                StartupRegistration.SetEnabled(Bool(change.Value));
                return current;
            }

            var options = current.Options;
            var updated = change.Name switch
            {
                "computerName" => options with { ComputerName = String(change.Value) },
                "preferredIp" => options with { PreferredIp = String(change.Value) },
                "desktopTheme" => options with { DesktopTheme = String(change.Value) },
                "webTheme" => options with { WebTheme = String(change.Value) },
                "language" => options with { Language = String(change.Value) },
                "minimizeTo" => options with { MinimizeTo = String(change.Value) },
                "closeTo" => options with { CloseTo = String(change.Value) },
                "showSharedGpuMemory" => options with { ShowSharedGpuMemory = Bool(change.Value) },
                "webEnabled" => options with { WebEnabled = Bool(change.Value) },
                "webLan" => options with { WebLan = Bool(change.Value) },
                "webPort" => options with { WebPort = Port(change.Value) },
                "apiEnabled" => options with { ApiEnabled = Bool(change.Value) },
                "apiLan" => options with { ApiLan = Bool(change.Value) },
                "apiPort" => options with { ApiPort = Port(change.Value) },
                "windowsNotifications" => options with { WindowsNotifications = Bool(change.Value) },
                "webNotifications" => options with { WebNotifications = Bool(change.Value) },
                "webMuteLocal" => options with { WebMuteLocal = Bool(change.Value) },
                "webMuteIps" => options with { WebMuteIps = Bool(change.Value) },
                "webMutedAddresses" => options with { WebMutedAddresses = String(change.Value) },
                "cpuAlertCelsius" => options with { CpuAlertCelsius = AlertNumber(change.Value) },
                "gpuAlertCelsius" => options with { GpuAlertCelsius = AlertNumber(change.Value) },
                "diskAlertPercent" => options with { DiskAlertPercent = AlertNumber(change.Value) },
                "alertDelaySeconds" => options with { AlertDelaySeconds = AlertNumber(change.Value) },
                "webPassword" or "apiPassword" => options,
                _ => throw new ArgumentException("未知的设置。")
            };
            return SaveCore(current, updated, change.Name == "webPassword" ? String(change.Value) : null,
                change.Name == "apiPassword" ? String(change.Value) : null);
        }
    }

    private static string String(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new ArgumentException("设置值必须是文本。");

    private static bool Bool(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? value.GetBoolean() : throw new ArgumentException("设置值必须是开关。");

    private static int Port(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var port) && port is >= 1 and <= 65535
            ? port : throw new ArgumentException("端口必须是 1 至 65535 的整数。");

    private static int AlertNumber(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number :
            throw new ArgumentException("告警阈值和持续时间必须是整数。");

    internal static IPAddress NormalizeAddress(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    internal static IPAddress[] MutedAddresses(string? value)
    {
        if (value is null || value.Length > 8192)
            throw new ArgumentException("静音列表过长或无效。");
        var addresses = new HashSet<IPAddress>();
        foreach (var entry in value.Split([' ', ',', ';', '，', '；', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!IPAddress.TryParse(entry, out var address))
                throw new ArgumentException("静音列表只能填写 IPv4 或 IPv6 地址。");
            addresses.Add(NormalizeAddress(address));
        }
        return addresses.ToArray();
    }

    private SettingsSnapshot SaveCore(SettingsSnapshot current, MonitorOptions? options, string? webPassword, string? apiPassword)
    {
        if (options is not { ComputerName: not null, PreferredIp: not null })
            throw new ArgumentException("设置内容不完整。");

        options = options with { ComputerName = options.ComputerName.Trim(), PreferredIp = options.PreferredIp.Trim() };
        Validate(options);
        options = options with { WebMutedAddresses = string.Join("\n", MutedAddresses(options.WebMutedAddresses).AsEnumerable()) };
        if (IPAddress.TryParse(options.PreferredIp, out var preferred))
            options = options with { PreferredIp = preferred.ToString() };

        if (options == current.Options && webPassword is null && apiPassword is null && LoadError is null)
            return current;

        return Persist(current with
        {
            Revision = current.Revision + 1,
            Options = options,
            WebPassword = ChangePassword(current.WebPassword, webPassword),
            ApiPassword = ChangePassword(current.ApiPassword, apiPassword)
        });
    }

    internal void SaveRemote(RemoteTarget target, bool remove)
    {
        lock (_gate)
        {
            var current = _current;
            var targets = current.RemoteTargets.Where(item => item.Id != target.Id);
            if (!remove) targets = targets.Append(target);
            Persist(current with { Revision = current.Revision + 1, RemoteTargets = targets.ToArray() });
        }
    }

    private SettingsSnapshot Persist(SettingsSnapshot next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(next, Json));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }

        LoadError = null;
        Volatile.Write(ref _current, next);
        return next;
    }

    private static void Validate(MonitorOptions? options)
    {
        if (options is not { ComputerName: not null, PreferredIp: not null } ||
            options.ComputerName.Length > 64 || options.ComputerName.Any(char.IsControl) ||
            options.PreferredIp.Length > 64 || (options.PreferredIp.Length != 0 && !IPAddress.TryParse(options.PreferredIp, out _)) ||
            options.DesktopTheme is not ("system" or "light" or "dark") || options.WebTheme is not ("system" or "light" or "dark") ||
            options.Language is not ("system" or "zh-CN" or "en") ||
            options.MinimizeTo is not ("taskbar" or "tray") || options.CloseTo is not ("tray" or "exit"))
            throw new ArgumentException("名称、IP 或选项无效。");
        if (options.WebPort is < 1 or > 65535 || options.ApiPort is < 1 or > 65535)
            throw new ArgumentException("端口必须是 1 至 65535 的整数。");
        if (options.CpuAlertCelsius is < 1 or > 150 || options.GpuAlertCelsius is < 1 or > 150 ||
            options.DiskAlertPercent is < 1 or > 100 || options.AlertDelaySeconds is < 1 or > 3600)
            throw new ArgumentException("温度阈值为 1 至 150 °C，剩余空间为 1 至 100%，持续时间为 1 至 3600 秒。");
        _ = MutedAddresses(options.WebMutedAddresses);
        if (options.WebPort == options.ApiPort)
            throw new ArgumentException("Web 与 API 需要使用不同端口。");
    }

    private static PasswordCredential? ChangePassword(PasswordCredential? existing, string? password)
    {
        if (password is null)
            return existing;
        if (password.Length == 0)
            return null;
        var bytes = System.Text.Encoding.UTF8.GetBytes(password);
        try { return PasswordCredential.Create(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

internal sealed record NetworkAddress(string Ip, string Adapter);

internal static class MonitorNetwork
{
    internal static NetworkAddress[] Addresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses
                    .Where(item => !item.Address.IsIPv6LinkLocal && !item.Address.IsIPv6Multicast && !IPAddress.IsLoopback(item.Address))
                    .Select(item => new NetworkAddress(item.Address.ToString(), adapter.Name)))
                .DistinctBy(item => item.Ip).OrderBy(item => item.Ip.Contains(':')).ThenBy(item => item.Adapter).ToArray();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    internal static string Url(string ip, int port, string path = "/") =>
        $"http://{(ip.Contains(':') ? $"[{ip}]" : ip)}:{port}{path}";
}
