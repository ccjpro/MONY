using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MONY;

internal sealed record CpuSensorReading(double? Temperature, double? Power);

internal sealed class CpuSensorAccess
{
    private const int CancelledByUser = 1223;
    private readonly object _gate = new();
    private CancellationTokenSource? _run;
    private CpuSensorReading? _reading;
    private string? _error;
    private bool _authorized;

    internal (bool Active, bool Authorized, CpuSensorReading? Reading, string? Error) Status
    {
        get { lock (_gate) return (_run is not null, _authorized, _reading, _error); }
    }

    internal void Start(bool requestAuthorization = true)
    {
        lock (_gate)
        {
            if (_run is not null) return;
            var run = new CancellationTokenSource();
            _run = run;
            _error = null;
            _ = Task.Run(() => ReadAsync(run, requestAuthorization));
        }
    }

    internal void Stop()
    {
        lock (_gate)
        {
            _run?.Cancel();
            _run = null;
            _reading = null;
            _error = null;
        }
    }

    internal async Task RevokeAsync()
    {
        try
        {
            var result = await Task.Run(() => CpuSensorTask.RequestAuthorizationAsync(CpuSensorTask.CurrentUser(),
                revoke: true, CancellationToken.None)).ConfigureAwait(false);
            if (result != 0) throw new IOException("CPU sensor authorization could not be removed.");
            Stop();
            lock (_gate) _authorized = false;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == CancelledByUser)
        {
            lock (_gate) _error = "已取消 UAC 授权。";
        }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException or COMException)
        {
            Debug.WriteLine(error);
            lock (_gate) _error = "无法撤销 CPU 采集授权。";
        }
    }

    private async Task ReadAsync(CancellationTokenSource run, bool requestAuthorization)
    {
        string? error = null;
        try
        {
            var user = CpuSensorTask.CurrentUser();
            var authorized = CpuSensorTask.Exists(user);
            lock (_gate)
                if (ReferenceEquals(_run, run)) _authorized = authorized;
            if (!authorized)
            {
                if (!requestAuthorization) return;
                var result = await CpuSensorTask.RequestAuthorizationAsync(user, revoke: false, run.Token).ConfigureAwait(false);
                if (result is 0 or 3010)
                {
                    lock (_gate)
                        if (ReferenceEquals(_run, run)) _authorized = true;
                }
                if (result != 0)
                {
                    error = result switch
                    {
                        3010 => "PawnIO 已安装。重启 Windows 后将自动采集。",
                        2 => "内置 PawnIO 驱动安装失败。",
                        _ => "无法保存 CPU 采集授权。"
                    };
                    return;
                }
            }

            var packet = new byte[CpuSensorTask.PacketSize];
            while (!run.IsCancellationRequested)
            {
                CpuSensorTask.Launch(user);
                using var pipe = CpuSensorTask.OpenPipe(user);
                try
                {
                    await pipe.ConnectAsync(6000, run.Token).ConfigureAwait(false);
                    if (!CpuSensorTask.TrustedPipe(pipe))
                        throw new UnauthorizedAccessException("Unexpected CPU sensor pipe owner.");
                    while (!run.IsCancellationRequested)
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(12));
                        var reading = await CpuSensorTask.ReadAsync(packet, pipe, timeout.Token).ConfigureAwait(false);
                        lock (_gate)
                            if (ReferenceEquals(_run, run)) _reading = reading;
                    }
                }
                catch (IOException)
                {
                    lock (_gate)
                        if (ReferenceEquals(_run, run)) _reading = null;
                    await Task.Delay(500, run.Token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    await Task.Delay(500, run.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested) { }
        catch (Win32Exception ex) when (ex.NativeErrorCode == CancelledByUser) { error = "已取消 UAC 授权。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or COMException or
            OperationCanceledException or InvalidOperationException or System.Security.SecurityException)
        {
            Debug.WriteLine(ex);
            error = "CPU 传感器进程未能持续采集，可重新授权。";
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_run, run))
                {
                    _run = null;
                    _reading = null;
                    _error = error;
                }
                run.Dispose();
            }
        }
    }
}
