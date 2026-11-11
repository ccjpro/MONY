using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System.Runtime.InteropServices;

namespace MONY;

internal static class Program
{
    internal static string Version { get; } = typeof(Program).Assembly.GetName().Version!.ToString(3);

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.FirstOrDefault() == CpuSensorTask.ConfigureArgument)
        {
            Environment.ExitCode = CpuSensorTask.Configure(args);
            return;
        }
        if (args.FirstOrDefault() == CpuSensorTask.WorkerArgument)
        {
            CpuSensorTask.RunWorker(args);
            return;
        }

        var startup = args.Contains("--startup", StringComparer.OrdinalIgnoreCase);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var instance = AppInstance.FindOrRegisterForKey("MONY");
        if (!instance.IsCurrent)
        {
            if (startup)
                return;

            AllowSetForegroundWindow(instance.ProcessId);
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            Task.Run(async () => await instance.RedirectActivationToAsync(activation)).GetAwaiter().GetResult();
            return;
        }

        Application.Start(_ =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
            var app = new App(startup);
            instance.Activated += (_, _) => dispatcher.TryEnqueue(app.ShowDashboard);
        });
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
