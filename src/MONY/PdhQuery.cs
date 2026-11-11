using System.Runtime.InteropServices;

namespace MONY;

internal sealed class PdhQuery : IDisposable
{
    private const uint MoreData = 0x800007D2;
    private const uint FormatDouble = 0x00000200;
    private const uint NoCap100 = 0x00008000;
    private IntPtr _query;
    private IntPtr _buffer;
    private uint _bufferSize;

    internal PdhQuery()
    {
        if (PdhOpenQuery(null, UIntPtr.Zero, out _query) != 0)
            _query = IntPtr.Zero;
    }

    internal IntPtr Add(string path) =>
        _query != IntPtr.Zero && PdhAddEnglishCounter(_query, path, UIntPtr.Zero, out var counter) == 0
            ? counter : IntPtr.Zero;

    internal bool Collect() => _query != IntPtr.Zero && PdhCollectQueryData(_query) == 0;

    internal double? Read(IntPtr counter) =>
        counter != IntPtr.Zero && PdhGetFormattedCounterValue(counter, FormatDouble | NoCap100, IntPtr.Zero, out var value) == 0
            && value.IsValid ? value.Value : null;

    internal List<(string Name, double Value)> ReadInstances(IntPtr counter)
    {
        var instances = new List<(string, double)>();
        if (counter == IntPtr.Zero)
            return instances;

        var size = _bufferSize;
        var result = PdhGetFormattedCounterArray(counter, FormatDouble | NoCap100, ref size, out var count, _buffer);
        if (result == MoreData && size > _bufferSize && size <= int.MaxValue)
        {
            _buffer = Marshal.ReAllocHGlobal(_buffer, (IntPtr)size);
            _bufferSize = size;
            result = PdhGetFormattedCounterArray(counter, FormatDouble | NoCap100, ref size, out count, _buffer);
        }
        if (result != 0)
            return instances;

        var stride = Marshal.SizeOf<CounterItem>();
        for (var index = 0; index < count; index++)
        {
            var item = Marshal.PtrToStructure<CounterItem>(_buffer + index * stride);
            if (item.Data.IsValid && Marshal.PtrToStringUni(item.Name) is string name)
                instances.Add((name, item.Data.Value));
        }
        return instances;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
        Marshal.FreeHGlobal(_buffer);
        _buffer = IntPtr.Zero;
        _bufferSize = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue
    {
        private readonly uint _status;
        internal readonly double Value;
        internal bool IsValid => _status <= 1 && double.IsFinite(Value) && Value >= 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        internal IntPtr Name;
        internal CounterValue Data;
    }

    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQuery(string? source, UIntPtr data, out IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, UIntPtr data, out IntPtr counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out CounterValue value);

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArray(
        IntPtr counter, uint format, ref uint bufferSize, out uint count, IntPtr buffer);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(IntPtr query);
}
