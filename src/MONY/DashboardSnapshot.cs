using Microsoft.Win32;
using System.Text.Json.Serialization;

namespace MONY;

internal readonly record struct MetricPoint(double? Primary, double? Secondary = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Tertiary = null);

internal sealed record MetricLabel(string Text, int Series = 0)
{
    [JsonIgnore]
    public bool SharedGpuMemory { get; init; }
}

internal sealed record DashboardMetric(
    string Id, string Title, string Glyph, string Value, string Detail, string? Telemetry, double Maximum, MetricPoint[] Points)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? SecondaryMaximum { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MetricLabel[]? ValueLabels { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MetricLabel[]? TelemetryLabels { get; init; }
}

internal sealed record DashboardVolume(string Letter, string Name, string Capacity, double UsedPercent, string TotalCapacity, string Usage);

internal sealed record DashboardSnapshot(
    bool Paused, string Status, string SampleTime, DashboardMetric[] Metrics)
{
    internal const int HistoryLength = 60;

    public string Version => Program.Version;
    public bool HasSample => Sample is not null;
    public double? CpuFrequencyMHz => Sample?.CpuFrequency;
    public string ComputerName { get; init; } = "";
    public string ComputerIp { get; init; } = "";
    public string WebTheme { get; init; } = "system";
    public string Language { get; init; } = "system";
    public DashboardAccent? Accent { get; init; }
    public string TargetId { get; init; } = RemoteMonitors.Local;
    public TargetChoice[] Targets { get; init; } = [];
    public DashboardVolume[] Volumes { get; init; } = [];
    [JsonIgnore]
    public PerformanceSample? Sample { get; init; }


    private static readonly string CpuDetails = GetCpuDetails();

    internal static DashboardSnapshot Create(bool paused, Queue<PerformanceSample> history, string? cpuDetails = null)
    {
        var sample = history.LastOrDefault();
        var network = history.Select(s => new MetricPoint(s.Download, s.Upload)).ToArray();
        var networkMaximum = Math.Max(1024, network.Select(p => Math.Max(p.Primary ?? 0, p.Secondary ?? 0)).DefaultIfEmpty().Max());

        var metrics = new List<DashboardMetric>
        {
            new("cpu", "处理器 (CPU)", "\uE950", Percent(sample?.Cpu), cpuDetails ?? CpuDetails,
                $"频率 {Frequency(sample?.CpuFrequency)} · 封装温度 {Temperature(sample?.CpuTemperature)} · 封装功耗 {Power(sample?.CpuPower)}",
                100, history.Select(s => new MetricPoint(s.Cpu)).ToArray()),
            new("memory", "内存", "\uE964", Percent(sample?.Memory),
                sample?.MemoryUsed is ulong used && sample.MemoryTotal is ulong total
                    ? $"已用 {Bytes(used)} / {Bytes(total)}"
                    : "内存数据暂不可用",
                null,
                100, history.Select(s => new MetricPoint(s.Memory)).ToArray())
        };

        if (sample is not null)
        {
            for (var index = 0; index < sample.Gpus.Length; index++)
            {
                var gpu = sample.Gpus[index];
                var points = history.Select(s =>
                {
                    var adapter = s.Gpus.FirstOrDefault(g => g.Id == gpu.Id);
                    return new MetricPoint(adapter?.Load, adapter?.DedicatedUsed);
                }).ToArray();
                var load = Percent(gpu.Load);
                MetricLabel[] labels =
                [
                    new($"温度 {Temperature(gpu.Temperature)} · 功耗 {Power(gpu.Power)}\n"),
                    new($"专用显存 {Usage(gpu.DedicatedUsed, gpu.DedicatedTotal)}", 2),
                    new($" · 共享显存 {Usage(gpu.SharedUsed, gpu.SharedTotal)}") { SharedGpuMemory = true }
                ];
                metrics.Add(new DashboardMetric(gpu.Id, $"GPU {index}", "\uE7F4", load, gpu.Name, Text(labels), 100, points)
                {
                    ValueLabels = [new(load, 1)],
                    TelemetryLabels = labels,
                    SecondaryMaximum = Math.Max(Math.Max(1, gpu.DedicatedTotal ?? 0), points.Max(p => p.Secondary ?? 0))
                });
            }

            foreach (var disk in sample.Disks)
            {
                var name = disk.Size is ulong size ? $"{disk.Name} · {Bytes(size)}" : disk.Name;
                var points = history.Select(s =>
                {
                    var drive = s.Disks.FirstOrDefault(d => d.Id == disk.Id);
                    return new MetricPoint(drive?.Activity, drive?.Read, drive?.Write);
                }).ToArray();
                var activity = Percent(disk.Activity);
                MetricLabel[] labels = [new($"温度 {Temperature(disk.Temperature)}\n"),
                    new($"读取 {Rate(disk.Read)}", 2), new(" · "), new($"写入 {Rate(disk.Write)}", 3)];
                metrics.Add(new DashboardMetric(disk.Id, $"磁盘 {disk.Number}", "\uEDA2", activity, name, Text(labels), 100, points)
                {
                    ValueLabels = [new(activity, 1)],
                    TelemetryLabels = labels,
                    SecondaryMaximum = Math.Max(1024, points.Max(p => Math.Max(p.Secondary ?? 0, p.Tertiary ?? 0)))
                });
            }
        }

        var download = $"↓ {Rate(sample?.Download)}";
        var upload = $"↑ {Rate(sample?.Upload)}";
        MetricLabel[] networkLabels = [new(download, 1), new("\n"), new(upload, 2)];
        metrics.Add(new DashboardMetric("network", "网络", "\uE968", $"{download}   {upload}",
            "活动网卡合计", null, networkMaximum, network) { ValueLabels = networkLabels });
        return new DashboardSnapshot(
            paused,
            paused ? "已暂停" : sample is null ? "正在采样" : "",
            sample is null ? "" : $"更新于 {sample.Time:HH:mm:ss}",
            metrics.ToArray())
        {
            Sample = sample,
            Volumes = sample?.Volumes.Select(volume =>
            {
                var used = volume.TotalBytes - volume.FreeBytes;
                return new DashboardVolume(volume.Letter, string.IsNullOrWhiteSpace(volume.Name) ? "未命名" : volume.Name,
                    $"已用 {Bytes(used)} · 可用 {Bytes(volume.FreeBytes)} · 总计 {Bytes(volume.TotalBytes)}",
                    used * 100.0 / volume.TotalBytes, FormatCapacity(volume.TotalBytes),
                    $"已用 {FormatCapacity(used)} · 剩余 {FormatCapacity(volume.FreeBytes)}");
            }).ToArray() ?? []
        };
    }

    private static string FormatCapacity(long bytes)
    {
        const long unit = 1024;
        var (scale, suffix) = bytes switch
        {
            >= unit * unit * unit * unit * unit => (unit * unit * unit * unit * unit, "PB"),
            >= unit * unit * unit * unit => (unit * unit * unit * unit, "TB"),
            >= unit * unit * unit => (unit * unit * unit, "GB"),
            >= unit * unit => (unit * unit, "MB"),
            >= unit => (unit, "KB"),
            _ => (1L, "B")
        };
        return $"{Math.Truncate(bytes * 10m / scale) / 10m:0.#}{suffix}";
    }

    private static string Text(MetricLabel[] labels) => string.Concat(labels.Select(label => label.Text));

    internal DashboardSnapshot ForDisplay(bool showSharedGpuMemory)
    {
        if (showSharedGpuMemory)
            return this;

        return this with
        {
            Metrics = Metrics.Select(metric =>
            {
                if (metric.TelemetryLabels is not { } labels || !labels.Any(label => label.SharedGpuMemory))
                    return metric;
                var visible = labels.Where(label => !label.SharedGpuMemory).ToArray();
                return metric with { Telemetry = Text(visible), TelemetryLabels = visible };
            }).ToArray()
        };
    }

    internal DashboardSnapshot Resume() => this with
    {
        Paused = false,
        Status = "正在采样",
        SampleTime = "",
        Metrics = Metrics.Select(metric => metric with { Points = [] }).ToArray()
    };

    private static string Frequency(double? mhz) => mhz is > 0 ? $"{mhz / 1000:F2} GHz" : "—";
    private static string Temperature(double? celsius) => celsius.HasValue ? $"{celsius:F0} °C" : "—";
    private static string Power(double? watts) => watts.HasValue ? $"{watts:F1} W" : "—";

    private static string Usage(double? used, double? total) =>
        total is > 0 ? $"{Bytes(used)} / {Bytes(total)}" : Bytes(used);

    private static string Bytes(double? bytes) => bytes switch
    {
        null => "—",
        >= 1099511627776 => $"{bytes / 1099511627776:F2} TiB",
        >= 1073741824 => $"{bytes / 1073741824:F1} GiB",
        >= 1048576 => $"{bytes / 1048576:F0} MiB",
        >= 1024 => $"{bytes / 1024:F1} KiB",
        _ => $"{bytes:F0} B"
    };

    private static string GetCpuDetails()
    {
        var cpuName = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
            "ProcessorNameString", null) as string;
        var cores = $"{Environment.ProcessorCount} 个逻辑处理器";
        return string.IsNullOrWhiteSpace(cpuName) ? cores : $"{cores} · {cpuName.Trim()}";
    }

    private static string Percent(double? value) => value.HasValue ? $"{value.Value:F0}%" : "—";

    private static string Rate(double? bytesPerSecond) =>
        bytesPerSecond.HasValue ? $"{Bytes(bytesPerSecond)}/s" : "—";
}
