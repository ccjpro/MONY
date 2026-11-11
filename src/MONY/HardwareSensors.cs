using System.Diagnostics;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Gpu;
using LibreHardwareMonitor.Hardware.Storage;

namespace MONY;

internal sealed class HardwareSensors : IDisposable
{
    private readonly Computer _computer = new();
    private DisplayAdapter[] _adapters = [];
    private PhysicalDisk[] _disks = [];
    private long _lastInventory;
    private bool _opened;

    internal HardwareSensors()
    {
        TryRead(() => { _computer.Open(); _opened = true; });
        if (!_opened)
            return;

        TryRead(() => _computer.IsCpuEnabled = true);
        TryRead(() => _computer.IsGpuEnabled = true);
        StorageDevice.ThrottleInterval = TimeSpan.FromSeconds(4);
        TryRead(() => _computer.IsStorageEnabled = true);
    }

    internal IReadOnlyList<DisplayAdapter> Adapters => _adapters;
    internal IReadOnlyList<PhysicalDisk> Disks => _disks;
    internal IHardware[] Devices { get; private set; } = [];

    internal void Update()
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastInventory == 0 || Stopwatch.GetElapsedTime(_lastInventory, now) >= TimeSpan.FromSeconds(8))
        {
            var adapters = WindowsHardware.GetDisplayAdapters();
            if (_opened && _lastInventory != 0 && !adapters.Select(a => a.Id).SequenceEqual(_adapters.Select(a => a.Id)))
            {
                TryRead(() =>
                {
                    _computer.IsGpuEnabled = false;
                    _computer.IsGpuEnabled = true;
                });
            }

            _adapters = adapters;
            _disks = WindowsHardware.GetPhysicalDisks() ?? _disks;
            _lastInventory = now;
        }

        if (!_opened)
            return;

        TryRead(() => Devices = _computer.Hardware.ToArray());
        foreach (var device in Devices)
            UpdateDevice(device);
    }

    internal static void UpdateDevice(IHardware device)
    {
        TryRead(device.Update);
        foreach (var child in device.SubHardware)
            UpdateDevice(child);
    }

    internal static CpuSensorReading ReadCpu(IHardware[] processors)
    {
        var temperature = processors.Max(device => Read(device, SensorType.Temperature,
            "CPU Package", "Core (Tdie)", "Core (Tctl/Tdie)", "Core (Tctl)", "Core Max", "CPU Cores"));
        var packagePowers = processors.Select(device => Read(device, SensorType.Power, "CPU Package", "Package"))
            .OfType<double>().Where(power => power > 0).ToArray();
        return new(temperature, packagePowers.Length == 0 ? null : packagePowers.Sum());
    }

    internal GenericGpu? FindGpu(DisplayAdapter adapter)
    {
        if (adapter.DeviceId is null)
            return null;

        return Devices.OfType<GenericGpu>().FirstOrDefault(gpu =>
            gpu.DeviceId is string id && string.Equals(WindowsHardware.NormalizeDeviceId(id),
                adapter.DeviceId, StringComparison.OrdinalIgnoreCase));
    }

    internal static double? Read(IHardware? hardware, SensorType type, params string[] names)
    {
        if (hardware is null)
            return null;

        var sensors = hardware.Sensors;
        foreach (var name in names)
        {
            var reading = sensors.FirstOrDefault(sensor => sensor.SensorType == type &&
                string.Equals(sensor.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
            if (reading is float value && float.IsFinite(value) && (type == SensorType.Temperature || value >= 0))
                return value;
        }
        return null;
    }

    private static void TryRead(Action read)
    {
        try
        {
            read();
        }
        catch (Exception error)
        {
            // A vendor API or sensor can fail independently of the Windows counters.
            Debug.WriteLine(error);
        }
    }

    public void Dispose()
    {
        if (_opened)
            TryRead(_computer.Close);
    }
}
