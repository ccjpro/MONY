using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Storage;

namespace MONY;

internal sealed record GpuSample(
    string Id, string Name, double? Load, double? Temperature, double? Power,
    double? DedicatedUsed, double? DedicatedTotal, double? SharedUsed, double? SharedTotal);

internal sealed record DiskSample(
    string Id, uint Number, string Name, ulong? Size, double? Activity, double? Read, double? Write,
    double? Temperature);

internal sealed record VolumeSample(string Letter, string Name, long TotalBytes, long FreeBytes);

internal sealed record PerformanceSample(
    double? Cpu, double? CpuFrequency, double? CpuTemperature, double? CpuPower,
    double? Memory, ulong? MemoryUsed, ulong? MemoryTotal,
    GpuSample[] Gpus, DiskSample[] Disks, VolumeSample[] Volumes, double? Download, double? Upload, DateTimeOffset Time);

internal sealed class PerformanceSampler : IDisposable
{
    private readonly HardwareSensors _hardware;
    private readonly PdhQuery _query = new();
    private readonly IntPtr _cpu;
    private readonly IntPtr _cpuFallback;
    private readonly IntPtr _cpuFrequency;
    private readonly IntPtr _cpuPerformance;
    private readonly IntPtr _gpu;
    private readonly IntPtr _dedicated;
    private readonly IntPtr _shared;
    private readonly IntPtr _diskIdle;
    private readonly IntPtr _diskRead;
    private readonly IntPtr _diskWrite;
    private Dictionary<string, (long Received, long Sent)>? _previousNetwork;
    private long _previousNetworkTime;
    private long _lastDiskTemperature;
    private long _lastVolumeRead;
    private VolumeSample[] _volumes = [];
    private readonly Dictionary<string, double?> _diskTemperatures = new();

    internal PerformanceSampler(HardwareSensors hardware)
    {
        _hardware = hardware;
        _cpu = _query.Add(@"\Processor Information(_Total)\% Processor Utility");
        _cpuFallback = _query.Add(@"\Processor Information(_Total)\% Processor Time");
        _cpuFrequency = _query.Add(@"\Processor Information(*)\Processor Frequency");
        _cpuPerformance = _query.Add(@"\Processor Information(*)\% Processor Performance");
        _gpu = _query.Add(@"\GPU Engine(*)\Utilization Percentage");
        _dedicated = _query.Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
        _shared = _query.Add(@"\GPU Adapter Memory(*)\Shared Usage");
        _diskIdle = _query.Add(@"\PhysicalDisk(*)\% Idle Time");
        _diskRead = _query.Add(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
        _diskWrite = _query.Add(@"\PhysicalDisk(*)\Disk Write Bytes/sec");
        _query.Collect();
    }

    internal PerformanceSample Read()
    {
        _hardware.Update();
        var countersAvailable = _query.Collect();
        var cpuLoad = countersAvailable ? _query.Read(_cpu) ?? _query.Read(_cpuFallback) : null;
        if (cpuLoad.HasValue)
            cpuLoad = Math.Clamp(cpuLoad.Value, 0, 100);

        var processors = _hardware.Devices.Where(device => device.HardwareType == HardwareType.Cpu).ToArray();
        var coreClocks = processors.Where(device => HardwareSensors.Read(device, SensorType.Clock, "Bus Speed") is > 0)
            .SelectMany(device => device.Sensors)
            .Where(sensor => sensor.SensorType == SensorType.Clock && sensor.Name.Contains("Core #", StringComparison.OrdinalIgnoreCase) &&
                sensor.Value is > 0 and var clock && float.IsFinite(clock))
            .Select(sensor => (double?)sensor.Value);
        var frequency = coreClocks.Average() ?? ReadWindowsFrequency(countersAvailable);
        var cpuSensors = HardwareSensors.ReadCpu(processors);

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var memoryAvailable = GlobalMemoryStatusEx(ref memory) && memory.TotalPhysical > 0;
        var used = memory.TotalPhysical - Math.Min(memory.TotalPhysical, memory.AvailablePhysical);
        var (download, upload) = ReadNetwork();

        return new PerformanceSample(cpuLoad, frequency, cpuSensors.Temperature, cpuSensors.Power,
            memoryAvailable ? 100.0 * used / memory.TotalPhysical : null,
            memoryAvailable ? used : null, memoryAvailable ? memory.TotalPhysical : null,
            ReadGpus(countersAvailable), ReadDisks(countersAvailable), ReadVolumes(),
            download, upload, DateTimeOffset.Now);
    }

    private VolumeSample[] ReadVolumes()
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastVolumeRead != 0 && Stopwatch.GetElapsedTime(_lastVolumeRead, now) < TimeSpan.FromSeconds(5))
            return _volumes;

        _lastVolumeRead = now;
        var volumes = new List<VolumeSample>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is DriveType.Network or DriveType.NoRootDirectory or DriveType.Unknown || !drive.IsReady)
                    continue;

                var total = drive.TotalSize;
                if (total > 0)
                    volumes.Add(new VolumeSample(drive.Name.TrimEnd('\\'), drive.VolumeLabel,
                        total, Math.Clamp(drive.TotalFreeSpace, 0, total)));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A removable volume can disappear between enumeration and the capacity query.
            }
        }

        _volumes = volumes.OrderBy(volume => volume.Letter, StringComparer.OrdinalIgnoreCase).ToArray();
        return _volumes;
    }

    private double? ReadWindowsFrequency(bool countersAvailable)
    {
        // Windows supplies the frequency reference and measured performance ratio for each logical CPU.
        // Pair each core separately because hybrid CPUs have different references for P and E cores.
        var frequency = ReadInstances(_cpuFrequency, countersAvailable);
        var performance = ReadInstances(_cpuPerformance, countersAvailable).ToDictionary(item => item.Name, item => item.Value);
        var measured = frequency
            .Where(item => item.Value > 0 && !item.Name.EndsWith("_Total", StringComparison.OrdinalIgnoreCase))
            .Select(item => performance.TryGetValue(item.Name, out var ratio) && ratio > 0
                ? (double?)(item.Value * ratio / 100)
                : null);
        return measured.Average() ??
            frequency.Where(item => item.Value > 0 && item.Name.Equals("_Total", StringComparison.OrdinalIgnoreCase))
                .Select(item => (double?)item.Value).FirstOrDefault();
    }

    private GpuSample[] ReadGpus(bool countersAvailable)
    {
        var loads = ReadInstances(_gpu, countersAvailable)
            .Where(item => AdapterId(item.Name) is not null)
            .GroupBy(item => item.Name[item.Name.IndexOf("luid_", StringComparison.OrdinalIgnoreCase)..], StringComparer.OrdinalIgnoreCase)
            .GroupBy(engine => AdapterId(engine.Key)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(adapter => adapter.Key, adapter => Math.Clamp(adapter.Max(engine => engine.Sum(item => item.Value)), 0, 100),
                StringComparer.OrdinalIgnoreCase);
        var dedicated = GpuMemory(_dedicated);
        var shared = GpuMemory(_shared);

        return _hardware.Adapters.Select(adapter =>
        {
            var hardware = _hardware.FindGpu(adapter);
            var load = loads.TryGetValue(adapter.Id, out var busy) ? busy
                : HardwareSensors.Read(hardware, SensorType.Load, "GPU Core") ??
                    hardware?.Sensors.Where(sensor => sensor.SensorType == SensorType.Load &&
                        sensor.Name.StartsWith("D3D ", StringComparison.Ordinal) && sensor.Value is float value && float.IsFinite(value))
                        .Max(sensor => sensor.Value);
            var temperature = HardwareSensors.Read(hardware, SensorType.Temperature, "GPU Core", "GPU Hot Spot") ??
                WindowsHardware.ReadGpuTemperature(adapter.Luid);
            var power = HardwareSensors.Read(hardware, SensorType.Power, "GPU Board Power", "GPU Total", "GPU Package", "GPU Power", "GPU PPT");
            if (power == 0 && hardware?.HardwareType == HardwareType.GpuIntel)
                power = null;

            return new GpuSample(adapter.Id, adapter.Name, load.HasValue ? Math.Clamp(load.Value, 0, 100) : null, temperature, power,
                dedicated.TryGetValue(adapter.Id, out var dedicatedUsed) ? dedicatedUsed :
                    ToBytes(HardwareSensors.Read(hardware, SensorType.SmallData, "D3D Dedicated Memory Used", "GPU Memory Used")),
                adapter.DedicatedMemory,
                shared.TryGetValue(adapter.Id, out var sharedUsed) ? sharedUsed :
                    ToBytes(HardwareSensors.Read(hardware, SensorType.SmallData, "D3D Shared Memory Used")),
                adapter.SharedMemory);
        }).ToArray();

        Dictionary<string, double> GpuMemory(IntPtr counter) => ReadInstances(counter, countersAvailable)
            .Select(item => (Id: AdapterId(item.Name), item.Value)).Where(item => item.Id is not null)
            .GroupBy(item => item.Id!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Value), StringComparer.OrdinalIgnoreCase);
    }

    private DiskSample[] ReadDisks(bool countersAvailable)
    {
        var idle = DiskCounters(_diskIdle);
        var read = DiskCounters(_diskRead);
        var write = DiskCounters(_diskWrite);
        var storage = _hardware.Devices.OfType<StorageDevice>()
            .Where(device => device.Storage.DriveNumber >= 0)
            .DistinctBy(device => (uint)device.Storage.DriveNumber)
            .ToDictionary(device => (uint)device.Storage.DriveNumber);
        var numbers = _hardware.Disks.Select(disk => disk.Number).Concat(storage.Keys)
            .Concat(idle.Keys).Concat(read.Keys).Concat(write.Keys).Distinct().Order();
        var inventory = _hardware.Disks.ToDictionary(disk => disk.Number);
        var now = Stopwatch.GetTimestamp();
        if (_lastDiskTemperature == 0 || Stopwatch.GetElapsedTime(_lastDiskTemperature, now) >= TimeSpan.FromSeconds(4))
        {
            _diskTemperatures.Clear();
            _lastDiskTemperature = now;
        }
        return numbers.Where(number => inventory.GetValueOrDefault(number)?.IsVirtual != true).Select(number =>
        {
            var device = storage.GetValueOrDefault(number);
            var disk = inventory.GetValueOrDefault(number);
            var temperature = HardwareSensors.Read(device, SensorType.Temperature, "Composite Temperature", "Temperature");
            var identity = disk?.DeviceId ?? device?.Name ?? string.Empty;
            var id = $"disk-{number}-{StringComparer.OrdinalIgnoreCase.GetHashCode(identity):x8}";
            if (temperature is null or 0)
            {
                if (!_diskTemperatures.TryGetValue(id, out temperature))
                    _diskTemperatures[id] = temperature = WindowsHardware.ReadDiskTemperature(number);
            }
            return new DiskSample(id, number,
                disk?.Name ?? device?.Name ?? $"物理磁盘 {number}", disk?.Size,
                idle.TryGetValue(number, out var idleTime) ? Math.Clamp(100 - idleTime, 0, 100) : null,
                read.TryGetValue(number, out var reading) ? reading : null, write.TryGetValue(number, out var writing) ? writing : null,
                temperature);
        }).ToArray();

        Dictionary<uint, double> DiskCounters(IntPtr counter)
        {
            var values = new Dictionary<uint, double>();
            foreach (var (name, value) in ReadInstances(counter, countersAvailable))
            {
                var prefix = name.AsSpan();
                var space = prefix.IndexOf(' ');
                if (space >= 0)
                    prefix = prefix[..space];
                if (uint.TryParse(prefix, out var number))
                    values[number] = value;
            }
            return values;
        }
    }

    private List<(string Name, double Value)> ReadInstances(IntPtr counter, bool available) =>
        available ? _query.ReadInstances(counter) : [];

    private static string? AdapterId(string name)
    {
        const int luidLength = 26;
        var start = name.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        return start >= 0 && name.Length >= start + luidLength ? name.Substring(start, luidLength) : null;
    }

    private static double? ToBytes(double? mebibytes) => mebibytes * 1048576;

    private (double? Download, double? Upload) ReadNetwork()
    {
        NetworkInterface[] adapters;
        try
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            _previousNetwork = null;
            return (null, null);
        }

        var current = new Dictionary<string, (long Received, long Sent)>();
        var time = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_previousNetworkTime, time).TotalSeconds;
        double received = 0, sent = 0;
        var activeAdapters = 0;
        var matchedAdapters = 0;
        foreach (var adapter in adapters)
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            activeAdapters++;
            try
            {
                var stats = adapter.GetIPStatistics();
                var totals = (stats.BytesReceived, stats.BytesSent);
                current[adapter.Id] = totals;
                if (_previousNetwork is not null && _previousNetwork.TryGetValue(adapter.Id, out var previous) &&
                    totals.BytesReceived >= previous.Received && totals.BytesSent >= previous.Sent)
                {
                    matchedAdapters++;
                    received += totals.BytesReceived - previous.Received;
                    sent += totals.BytesSent - previous.Sent;
                }
            }
            catch (NetworkInformationException)
            {
                // An adapter can disappear between enumeration and reading its counters.
            }
        }

        _previousNetwork = current;
        _previousNetworkTime = time;
        if (activeAdapters == 0)
            return (0, 0);
        return matchedAdapters > 0 && elapsed > 0 ? (received / elapsed, sent / elapsed) : (null, null);
    }

    public void Dispose() => _query.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        internal uint Length;
        internal uint MemoryLoad;
        internal ulong TotalPhysical;
        internal ulong AvailablePhysical;
        internal ulong TotalPageFile;
        internal ulong AvailablePageFile;
        internal ulong TotalVirtual;
        internal ulong AvailableVirtual;
        internal ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
