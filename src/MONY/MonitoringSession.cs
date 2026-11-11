namespace MONY;

internal sealed class MonitoringSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Queue<PerformanceSample> _history = new(DashboardSnapshot.HistoryLength);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _sampling;
    private DashboardSnapshot _snapshot;
    private int _generation;

    internal MonitoringSession()
    {
        _snapshot = DashboardSnapshot.Create(false, _history);
        _sampling = Task.Run(() => SampleAsync(_stop.Token));
        CpuSensors.Start(requestAuthorization: false);
    }

    internal CpuSensorAccess CpuSensors { get; } = new();

    internal DashboardSnapshot Snapshot => Volatile.Read(ref _snapshot);

    internal DashboardSnapshot SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (paused == _snapshot.Paused)
                return _snapshot;

            _generation++;
            if (paused)
                return Publish(true);

            _history.Clear();
            var resumed = _snapshot.Resume();
            Volatile.Write(ref _snapshot, resumed);
            return resumed;
        }
    }

    private DashboardSnapshot Publish(bool paused)
    {
        var snapshot = DashboardSnapshot.Create(paused, _history);
        Volatile.Write(ref _snapshot, snapshot);
        return snapshot;
    }

    private async Task SampleAsync(CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        HardwareSensors? hardware = null;
        PerformanceSampler? sampler = null;
        var samplerGeneration = -1;
        try
        {
            do
            {
                bool paused;
                int generation;
                lock (_gate)
                {
                    paused = _snapshot.Paused;
                    generation = _generation;
                }

                if (samplerGeneration != generation)
                {
                    sampler?.Dispose();
                    sampler = paused ? null : new PerformanceSampler(hardware ??= new HardwareSensors());
                    samplerGeneration = generation;
                }

                if (sampler is null)
                    continue;

                var sample = sampler.Read();
                if (CpuSensors.Status.Reading is { } reading)
                    sample = sample with { CpuTemperature = sample.CpuTemperature ?? reading.Temperature,
                        CpuPower = sample.CpuPower ?? reading.Power };
                lock (_gate)
                {
                    if (_generation == generation)
                    {
                        if (_history.Count == DashboardSnapshot.HistoryLength)
                            _history.Dequeue();
                        _history.Enqueue(sample);
                        Publish(false);
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            sampler?.Dispose();
            hardware?.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        CpuSensors.Stop();
        _stop.Cancel();
        try
        {
            await _sampling.ConfigureAwait(false);
        }
        finally
        {
            _stop.Dispose();
        }
    }
}
