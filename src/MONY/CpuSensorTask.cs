using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace MONY;

internal static class CpuSensorTask
{
    internal const string ConfigureArgument = "--configure-cpu-sensors";
    internal const string WorkerArgument = "--cpu-sensor-task";
    internal const int PacketSize = 2 * sizeof(double);
    private const string Prefix = "MONY.CpuSensors-";
    private const string DriverHash = "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032";
    private static readonly SecurityIdentifier SystemUser = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    internal static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!;
    }

    internal static bool DriverAvailable
    {
        get
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
                if (Version.TryParse(key?.GetValue("DisplayVersion") as string, out var version) && version >= new Version(2, 2))
                    return true;
            }
            return false;
        }
    }

    private static dynamic TaskFolder()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler is unavailable.");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service.GetFolder("\\");
    }

    internal static bool Exists(SecurityIdentifier user)
    {
        try
        {
            dynamic task = TaskFolder().GetTask(Prefix + user.Value);
            string principal = task.Definition.Principal.UserId;
            return string.Equals(principal, "SYSTEM", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(principal, @"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase) || principal == SystemUser.Value;
        }
        catch (FileNotFoundException) { return false; }
        catch (COMException error) when ((uint)error.HResult == 0x80070002) { return false; }
    }

    internal static void Launch(SecurityIdentifier user) => TaskFolder().GetTask(Prefix + user.Value).Run(null);

    internal static async Task<int> RequestAuthorizationAsync(SecurityIdentifier user, bool revoke, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        start.ArgumentList.Add(ConfigureArgument);
        start.ArgumentList.Add(user.Value);
        if (revoke) start.ArgumentList.Add("--revoke");
        cancellation.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("CPU sensor authorization could not start.");
        await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
        return process.ExitCode;
    }

    internal static async Task<CpuSensorReading> ReadAsync(byte[] packet, NamedPipeClientStream pipe,
        CancellationToken cancellation)
    {
        await pipe.ReadExactlyAsync(packet, cancellation).ConfigureAwait(false);
        var temperature = BinaryPrimitives.ReadDoubleLittleEndian(packet);
        var power = BinaryPrimitives.ReadDoubleLittleEndian(packet.AsSpan(sizeof(double)));
        return new(double.IsFinite(temperature) ? temperature : null, double.IsFinite(power) && power > 0 ? power : null);
    }

    internal static NamedPipeClientStream OpenPipe(SecurityIdentifier user) =>
        new(".", Prefix + user.Value, PipeDirection.In, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);

    internal static bool TrustedPipe(NamedPipeClientStream pipe) =>
        Equals(pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)), SystemUser);

    internal static int Configure(string[] args)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) ||
            args.Length is < 2 or > 3 || !TryUser(args[1], out var user))
            return 1;
        var directory = new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Prefix + user.Value));
        try
        {
            dynamic folder = TaskFolder();
            var taskName = Prefix + user.Value;
            if (args.Length == 3)
            {
                if (args[2] != "--revoke") return 1;
                if (Exists(user))
                {
                    dynamic task = folder.GetTask(taskName);
                    if ((int)task.State is 2 or 4) task.Stop(0);
                    folder.DeleteTask(taskName, 0);
                }
                if (directory.Exists && IsProtected(directory)) directory.Delete(recursive: true);
                return 0;
            }

            if (Exists(user)) return 0;
            var driver = InstallDriver();
            if (driver is not (0 or 3010)) return 2;

            var security = new DirectorySecurity();
            security.SetOwner(Administrators);
            security.SetAccessRuleProtection(true, false);
            foreach (var account in new[] { Administrators, SystemUser })
                security.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));

            if (directory.Exists)
            {
                if (!IsProtected(directory)) return 1;
                directory.Delete(recursive: true);
            }
            directory.Create(security);
            if (!IsProtected(directory)) return 1;

            try
            {
                var executable = Path.Combine(directory.FullName, "MONY.Sensor.exe");
                using (var source = File.OpenRead(Environment.ProcessPath!))
                using (var destination = File.Open(executable, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    source.CopyTo(destination);

                var script = Path.Combine(directory.FullName, "sensor.cmd");
                File.WriteAllText(script,
                    "@echo off\r\nset \"DOTNET_BUNDLE_EXTRACT_BASE_DIR=%~dp0runtime\"\r\n" +
                    $"\"%~dp0MONY.Sensor.exe\" {WorkerArgument} {user.Value}\r\n");

                dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
                scheduler.Connect();
                dynamic definition = scheduler.NewTask(0);
                definition.RegistrationInfo.Description = "MONY read-only CPU temperature and package power sensor";
                definition.Principal.UserId = "SYSTEM";
                definition.Principal.LogonType = 5; // TASK_LOGON_SERVICE_ACCOUNT
                definition.Principal.RunLevel = 1;
                definition.Settings.AllowDemandStart = true;
                definition.Settings.DisallowStartIfOnBatteries = false;
                definition.Settings.StopIfGoingOnBatteries = false;
                definition.Settings.ExecutionTimeLimit = "PT0S";
                definition.Settings.MultipleInstances = 2; // TASK_INSTANCES_IGNORE_NEW
                dynamic action = definition.Actions.Create(0);
                action.Path = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                action.Arguments = $"/d /s /c \"\"{script}\"\"";
                action.WorkingDirectory = directory.FullName;
                folder.RegisterTaskDefinition(taskName, definition, 6, "SYSTEM", null, 5,
                    $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;{user.Value})");
                return driver;
            }
            catch
            {
                if (directory.Exists) directory.Delete(recursive: true);
                throw;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException or
            System.Security.SecurityException or InvalidOperationException)
        {
            Debug.WriteLine(error);
            return 1;
        }
    }

    private static bool TryUser(string text, out SecurityIdentifier user)
    {
        try
        {
            user = new SecurityIdentifier(text);
            return user.Value.StartsWith("S-1-5-21-", StringComparison.Ordinal) ||
                user.Value.StartsWith("S-1-12-1-", StringComparison.Ordinal);
        }
        catch (ArgumentException) { user = null!; return false; }
    }

    private static bool IsProtected(DirectoryInfo directory)
    {
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        var security = directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        return (Equals(owner, Administrators) || Equals(owner, SystemUser)) && security.AreAccessRulesProtected &&
            security.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().All(rule =>
                rule.AccessControlType == AccessControlType.Deny ||
                Equals(rule.IdentityReference, Administrators) || Equals(rule.IdentityReference, SystemUser));
    }

    private static int InstallDriver()
    {
        if (DriverAvailable) return 0;
        var path = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, "PawnIO_setup.exe");
        using var installer = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!Convert.ToHexString(SHA256.HashData(installer)).Equals(DriverHash, StringComparison.Ordinal)) return 2;

        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Environment.SystemDirectory };
        start.ArgumentList.Add("-install");
        start.ArgumentList.Add("-silent");
        using var setup = Process.Start(start);
        return setup is not null && setup.WaitForExit(110_000) ? setup.ExitCode : 2;
    }

    internal static void RunWorker(string[] args)
    {
        if (args.Length != 2 || !CurrentUser().Equals(SystemUser) || !TryUser(args[1], out var user))
            return;
        try
        {
            var security = new PipeSecurity();
            security.SetOwner(SystemUser);
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            security.AddAccessRule(new PipeAccessRule(SystemUser, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.Read | PipeAccessRights.Synchronize, AccessControlType.Allow));
            using var pipe = NamedPipeServerStreamAcl.Create(Prefix + user.Value, PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, PacketSize, security);
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                pipe.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();

            var computer = new Computer { IsCpuEnabled = true };
            try
            {
                computer.Open();
                var processors = computer.Hardware.Where(device => device.HardwareType == HardwareType.Cpu).ToArray();
                var packet = new byte[PacketSize];
                while (true)
                {
                    foreach (var processor in processors) HardwareSensors.UpdateDevice(processor);
                    var reading = HardwareSensors.ReadCpu(processors);
                    BinaryPrimitives.WriteDoubleLittleEndian(packet, reading.Temperature ?? double.NaN);
                    BinaryPrimitives.WriteDoubleLittleEndian(packet.AsSpan(sizeof(double)), reading.Power ?? double.NaN);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    pipe.WriteAsync(packet, timeout.Token).AsTask().GetAwaiter().GetResult();
                    Thread.Sleep(1000);
                }
            }
            finally { computer.Close(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or
            System.Security.SecurityException or InvalidOperationException)
        {
            Debug.WriteLine(error);
        }
    }
}
