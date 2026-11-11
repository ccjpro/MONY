using System.Buffers.Binary;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MONY;

internal sealed record DisplayAdapter(
    string Id, string Name, ulong DedicatedMemory, ulong SharedMemory, string? DeviceId, AdapterLuid Luid);
internal sealed record PhysicalDisk(uint Number, string Name, ulong? Size, string? DeviceId, bool IsVirtual);

[StructLayout(LayoutKind.Sequential)]
internal struct AdapterLuid
{
    internal uint LowPart;
    internal int HighPart;
    internal readonly string CounterId => $"luid_0x{unchecked((uint)HighPart):x8}_0x{LowPart:x8}";
}

internal static class WindowsHardware
{
    private static readonly Guid FactoryId = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid DisplayInterface = new("1ca05180-a699-450a-9a0c-de4fbe3ddd89");

    internal static unsafe DisplayAdapter[] GetDisplayAdapters()
    {
        const int notFound = unchecked((int)0x887A0002);
        var result = new List<(DisplayAdapter Adapter, bool HasInterface)>();
        if (CreateDXGIFactory1(in FactoryId, out var factory) < 0)
            return [];

        try
        {
            var deviceIds = GetDisplayDeviceIds();
            // IDXGIFactory1::EnumAdapters1 and IDXGIAdapter1::GetDesc1 follow the inherited COM methods.
            var enumerate = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)factory)[12];
            for (uint index = 0; ; index++)
            {
                nint adapter;
                var status = enumerate(factory, index, &adapter);
                if (status == notFound || status < 0)
                    break;

                try
                {
                    AdapterDescription description;
                    var getDescription = (delegate* unmanaged[Stdcall]<nint, AdapterDescription*, int>)(*(nint**)adapter)[10];
                    if (getDescription(adapter, &description) < 0 || (description.Flags & 2) != 0)
                        continue;

                    var luid = description.Luid;
                    var id = luid.CounterId;
                    var deviceId = deviceIds.GetValueOrDefault(id);
                    var hasInterface = deviceId is not null;
                    if (!QueryPhysicalAdapter(luid, ref deviceId))
                        continue;

                    result.Add((new DisplayAdapter(id, new string(description.Name), description.DedicatedVideoMemory,
                        description.SharedSystemMemory, deviceId, luid), hasInterface));
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
        }
        finally
        {
            Marshal.Release(factory);
        }

        // Windows can expose a render alias for the same hardware device. Keep the PnP-linked adapter.
        return result.GroupBy(item => item.Adapter.DeviceId ?? item.Adapter.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.HasInterface).First().Adapter).ToArray();
    }

    private static unsafe bool QueryPhysicalAdapter(AdapterLuid luid, ref string? deviceId)
    {
        const uint adapterType = 15;
        const uint physicalAdapterPnpKey = 41;
        const uint excludedFlags = (1 << 2) | (1 << 6) | (1 << 7); // Software, indirect display, paravirtualized.
        var adapter = new OpenAdapterByLuid { Luid = luid };
        if (D3DKMTOpenAdapterFromLuid(ref adapter) != 0)
            return true;

        try
        {
            uint flags = 0;
            if (QueryAdapter(adapter.Handle, adapterType, &flags, sizeof(uint)) && (flags & excludedFlags) != 0)
                return false;

            if (deviceId is null)
            {
                char* buffer = stackalloc char[512];
                new Span<char>(buffer, 512).Clear();
                uint capacity = 512;
                var key = new AdapterPnpKey { KeyType = 1, Destination = buffer, Length = &capacity };
                if (QueryAdapter(adapter.Handle, physicalAdapterPnpKey, &key, (uint)sizeof(AdapterPnpKey)) &&
                    capacity > 0 && capacity <= 512)
                {
                    var value = new string(buffer, 0, (int)capacity).TrimEnd('\0');
                    var root = value.IndexOf(@"\Enum\", StringComparison.OrdinalIgnoreCase);
                    deviceId = NormalizeDeviceId(root >= 0 ? value[(root + 6)..] : value);
                }
            }
            return true;
        }
        finally
        {
            D3DKMTCloseAdapter(in adapter.Handle);
        }
    }

    internal static unsafe double? ReadGpuTemperature(AdapterLuid luid)
    {
        const uint adapterPerfData = 62;
        var adapter = new OpenAdapterByLuid { Luid = luid };
        if (D3DKMTOpenAdapterFromLuid(ref adapter) != 0)
            return null;

        try
        {
            var data = new AdapterPerformance();
            return QueryAdapter(adapter.Handle, adapterPerfData, &data, (uint)sizeof(AdapterPerformance)) &&
                data.Temperature is > 0 and <= 1500 ? data.Temperature / 10.0 : null;
        }
        finally
        {
            D3DKMTCloseAdapter(in adapter.Handle);
        }
    }

    private static unsafe bool QueryAdapter(uint handle, uint type, void* data, uint size)
    {
        var query = new AdapterQuery { Handle = handle, Type = type, Data = data, Size = size };
        return D3DKMTQueryAdapterInfo(in query) == 0;
    }

    internal static PhysicalDisk[]? GetPhysicalDisks()
    {
        try
        {
            using var search = new ManagementObjectSearcher(new ManagementScope(@"root\CIMV2"),
                new ObjectQuery("SELECT Index, Model, Size, PNPDeviceID FROM Win32_DiskDrive"),
                new System.Management.EnumerationOptions { ReturnImmediately = true, Timeout = TimeSpan.FromSeconds(3) });
            using var disks = search.Get();
            var result = new List<PhysicalDisk>();
            foreach (ManagementObject disk in disks)
            {
                using (disk)
                {
                    if (disk["Index"] is uint index && index != uint.MaxValue)
                    {
                        var name = (disk["Model"] as string)?.Trim();
                        var id = disk["PNPDeviceID"] as string;
                        result.Add(new PhysicalDisk(index, string.IsNullOrWhiteSpace(name) ? $"磁盘 {index}" : name,
                            disk["Size"] as ulong?, id, IsVirtualDisk(index, id)));
                    }
                }
            }
            return result.OrderBy(d => d.Number).ToArray();
        }
        catch (Exception error) when (error is ManagementException or COMException or UnauthorizedAccessException)
        {
            Debug.WriteLine(error);
            return null;
        }
    }

    private static bool IsVirtualDisk(uint number, string? id)
    {
        const int storageDeviceProperty = 0;
        var descriptor = ReadStorageProperty(number, storageDeviceProperty);
        var bus = descriptor.Length >= 32 ? BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(28)) : 0;
        return bus is 14 or 15 or 16 || id?.Contains("&PROD_VIRTUAL_DISK", StringComparison.OrdinalIgnoreCase) == true;
    }

    internal static double? ReadDiskTemperature(uint number)
    {
        const int storageDeviceTemperature = 52;
        const int storageAdapterTemperature = 51;
        return ReadTemperature(ReadStorageProperty(number, storageDeviceTemperature)) ??
            ReadTemperature(ReadStorageProperty(number, storageAdapterTemperature));

        static double? ReadTemperature(byte[] data)
        {
            const int infoOffset = 24;
            const int infoSize = 16;
            if (data.Length < infoOffset + infoSize)
                return null;

            var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12));
            double? fallback = null;
            for (var index = 0; index < count && infoOffset + (index + 1) * infoSize <= data.Length; index++)
            {
                var info = data.AsSpan(infoOffset + index * infoSize, infoSize);
                var value = BinaryPrimitives.ReadInt16LittleEndian(info[2..]);
                if (value is < -40 or > 150)
                    continue;

                if (BinaryPrimitives.ReadUInt16LittleEndian(info) == 0)
                    return value;
                fallback = Math.Max(fallback ?? value, value);
            }
            return fallback;
        }
    }

    private static byte[] ReadStorageProperty(uint number, int property)
    {
        const uint openExisting = 3;
        const uint shareReadAndWrite = 3;
        const uint queryProperty = 0x002D1400;
        using var disk = CreateFile($@"\\.\PhysicalDrive{number}", 0, shareReadAndWrite, IntPtr.Zero, openExisting, 0, IntPtr.Zero);
        if (disk.IsInvalid)
            return [];

        var query = new byte[12]; // STORAGE_PROPERTY_QUERY with PropertyStandardQuery.
        BinaryPrimitives.WriteInt32LittleEndian(query, property);
        var data = new byte[1024];
        return DeviceIoControl(disk, queryProperty, query, query.Length, data, data.Length, out var length, IntPtr.Zero)
            ? data[..length] : [];
    }

    internal static string NormalizeDeviceId(string id)
    {
        if (id.StartsWith(@"\\?\", StringComparison.Ordinal))
            id = id[4..];
        var suffix = id.LastIndexOf("#{", StringComparison.Ordinal);
        if (suffix >= 0)
            id = id[..suffix];
        return id.Replace('#', '\\').TrimEnd('\\', '\0');
    }

    private static Dictionary<string, string> GetDisplayDeviceIds()
    {
        const uint present = 0;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (CM_Get_Device_Interface_List_Size(out var length, in DisplayInterface, null, present) != 0 || length <= 1)
            return result;

        var devices = new char[length];
        if (CM_Get_Device_Interface_List(in DisplayInterface, null, devices, length, present) != 0)
            return result;

        foreach (var name in new string(devices).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var adapter = new OpenAdapter { Name = name };
            if (D3DKMTOpenAdapterFromDeviceName(ref adapter) != 0)
                continue;

            result[adapter.Luid.CounterId] = NormalizeDeviceId(name);
            D3DKMTCloseAdapter(in adapter.Handle);
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct AdapterDescription
    {
        internal fixed char Name[128];
        internal uint VendorId, DeviceId, SubSystemId, Revision;
        internal nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        internal AdapterLuid Luid;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAdapter
    {
        [MarshalAs(UnmanagedType.LPWStr)] internal string Name;
        internal uint Handle;
        internal AdapterLuid Luid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenAdapterByLuid
    {
        internal AdapterLuid Luid;
        internal uint Handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct AdapterQuery
    {
        internal uint Handle, Type;
        internal void* Data;
        internal uint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct AdapterPnpKey
    {
        internal uint PhysicalIndex, KeyType;
        internal char* Destination;
        internal uint* Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterPerformance
    {
        internal uint PhysicalIndex;
        internal ulong MemoryFrequency, MaxMemoryFrequency, MaxMemoryFrequencyOc, MemoryBandwidth, PcieBandwidth;
        internal uint FanRpm, Power, Temperature;
        internal byte PowerStateOverride;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition,
        uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize,
        [Out] byte[] output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapterByLuid adapter);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int D3DKMTQueryAdapterInfo(in AdapterQuery query);

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid iid, out nint factory);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List_Size(out uint length, in Guid interfaceClass, string? device, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List(in Guid interfaceClass, string? device, [Out] char[] list, uint length, uint flags);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int D3DKMTOpenAdapterFromDeviceName(ref OpenAdapter adapter);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int D3DKMTCloseAdapter(in uint adapter);
}
