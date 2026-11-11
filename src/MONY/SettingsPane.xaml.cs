using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MONY;

public sealed partial class SettingsPane : UserControl
{
    private readonly SemaphoreSlim _updates = new(1);
    private MonitorServices? _services;
    private MonitoringSession? _session;
    private Action<string>? _message;
    private bool _loading;
    private bool _revokingSensors;
    private bool _changingRemote;
    private RemoteTargetView[] _remoteTargets = [];

    public SettingsPane() => InitializeComponent();

    internal void Open(MonitorServices services, MonitoringSession session, Action<string> message)
    {
        _services = services;
        _session = session;
        _message = message;
        var view = services.View();
        AppVersion.Text = view.Version;
        _loading = true;
        try
        {
            var options = view.Options;
            ComputerName.Text = options.ComputerName;
            WebPort.Text = options.WebPort.ToString(CultureInfo.InvariantCulture);
            ApiPort.Text = options.ApiPort.ToString(CultureInfo.InvariantCulture);
            PreferredIp.Items.Clear();
            PreferredIp.Items.Add(new ComboBoxItem { Tag = "", Content = UiText.T("自动选择") });
            foreach (var address in view.Addresses)
                PreferredIp.Items.Add(new ComboBoxItem { Tag = address.Ip, Content = address.Ip });
            if (options.PreferredIp.Length > 0 && !view.Addresses.Any(address => address.Ip == options.PreferredIp))
                PreferredIp.Items.Add(new ComboBoxItem { Tag = options.PreferredIp, Content = options.PreferredIp });
            Select(PreferredIp, options.PreferredIp);
            Select(DesktopTheme, options.DesktopTheme);
            Select(WebTheme, options.WebTheme);
            Select(DisplayLanguage, options.Language);
            Select(MinimizeTo, options.MinimizeTo);
            Select(CloseTo, options.CloseTo);
            StartWithWindows.IsOn = view.StartWithWindows;
            WebEnabled.IsOn = options.WebEnabled;
            WebLan.IsOn = options.WebLan;
            ShowSharedGpuMemory.IsOn = options.ShowSharedGpuMemory;
            ApiEnabled.IsOn = options.ApiEnabled;
            ApiLan.IsOn = options.ApiLan;
            WebPassword.Password = ApiPassword.Password = "";
            RefreshAlertSwitches(options);
            WebMutedAddresses.Text = options.WebMutedAddresses;
            CpuAlertCelsius.Text = options.CpuAlertCelsius.ToString(CultureInfo.InvariantCulture);
            GpuAlertCelsius.Text = options.GpuAlertCelsius.ToString(CultureInfo.InvariantCulture);
            DiskAlertPercent.Text = options.DiskAlertPercent.ToString(CultureInfo.InvariantCulture);
            AlertDelaySeconds.Text = options.AlertDelaySeconds.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _loading = false;
        }

        RefreshStatus(view);
        _message(view.LoadError ?? "");
    }

    internal void RefreshLanguage(Action translate)
    {
        _loading = true;
        try { translate(); }
        finally { _loading = false; }
        if (_services is { } services) RefreshStatus(services.View());
    }

    internal void RefreshStatus(SettingsView view)
    {
        if (_updates.CurrentCount != 0)
        {
            _loading = true;
            try
            {
                var options = view.Options;
                if (PreferredIp.Items.FirstOrDefault() is ComboBoxItem automatic)
                    automatic.Content = UiText.T("自动选择");
                if (!PreferredIp.Items.OfType<ComboBoxItem>().Any(item => Equals(item.Tag, options.PreferredIp)))
                    PreferredIp.Items.Add(new ComboBoxItem { Tag = options.PreferredIp, Content = options.PreferredIp });
                Select(PreferredIp, options.PreferredIp);
                Select(DesktopTheme, options.DesktopTheme);
                Select(WebTheme, options.WebTheme);
                Select(DisplayLanguage, options.Language);
                Select(MinimizeTo, options.MinimizeTo);
                Select(CloseTo, options.CloseTo);
                StartWithWindows.IsOn = view.StartWithWindows;
                WebEnabled.IsOn = options.WebEnabled;
                WebLan.IsOn = options.WebLan;
                ShowSharedGpuMemory.IsOn = options.ShowSharedGpuMemory;
                ApiEnabled.IsOn = options.ApiEnabled;
                ApiLan.IsOn = options.ApiLan;
                RefreshAlertSwitches(options);
            }
            finally
            {
                _loading = false;
            }
        }
        UpdateEndpoint(view.Web, view.Options.WebEnabled, view.Options.WebPort, view.WebUrl, WebStatus, WebUrl);
        UpdateEndpoint(view.Api, view.Options.ApiEnabled, view.Options.ApiPort, view.ApiUrl, ApiStatus, ApiUrl);
        WebPassword.PlaceholderText = UiText.T(view.WebPasswordSet ? "已设置" : "新密码");
        ApiPassword.PlaceholderText = UiText.T(view.ApiPasswordSet ? "已设置" : "新密码");
        WebClear.IsEnabled = view.WebPasswordSet;
        ApiClear.IsEnabled = view.ApiPasswordSet;
        RefreshCpuSensors();
        RefreshRemotes(view.RemoteTargets);
    }

    private void RefreshRemotes(RemoteTargetView[] targets)
    {
        if (!_remoteTargets.SequenceEqual(targets))
        {
            var selected = (RemoteList.SelectedItem as ComboBoxItem)?.Tag;
            _remoteTargets = targets;
            RemoteList.Items.Clear();
            foreach (var target in targets)
            {
                var item = new ComboBoxItem { Tag = target.Id, Content = target.Label };
                ToolTipService.SetToolTip(item, target.Address);
                RemoteList.Items.Add(item);
            }
            RemoteList.SelectedItem = RemoteList.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, selected))
                ?? RemoteList.Items.OfType<ComboBoxItem>().FirstOrDefault();
        }
        RemoteListRow.Visibility = targets.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ConnectRemoteButton.IsEnabled = RemoveRemoteButton.IsEnabled = !_changingRemote;
    }

    private async void ConnectRemote(object sender, RoutedEventArgs args)
    {
        if (_services is not { } services || _changingRemote) return;
        if (!int.TryParse(RemotePort.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            _message?.Invoke("端口必须是 1 至 65535 的整数。");
            return;
        }
        var connection = new RemoteConnection(null, RemotePassword.Password, RemoteAddress.Text, port);
        await ChangeRemote(async () =>
        {
            await services.Remotes.ConnectAsync(connection);
            if (RemoteAddress.Text == connection.Ip) RemoteAddress.Text = "";
            if (RemotePassword.Password == connection.Password) RemotePassword.Password = "";
        }, "连接成功，可在性能概览切换目标。");
    }

    private async void RemoveRemote(object sender, RoutedEventArgs args)
    {
        if (_services is not { } services || _changingRemote || RemoteList.SelectedItem is not ComboBoxItem { Tag: string id }) return;
        await ChangeRemote(() => Task.Run(() => services.Remotes.Remove(id)), "已移除连接。");
    }

    private async Task ChangeRemote(Func<Task> change, string success)
    {
        _changingRemote = true;
        RefreshRemotes(_remoteTargets);
        try
        {
            await change();
            _message?.Invoke(success);
        }
        catch (ArgumentException error) { _message?.Invoke(error.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { _message?.Invoke("无法保存监控连接。"); }
        finally
        {
            _changingRemote = false;
            if (_services is { } services) RefreshRemotes(services.View().RemoteTargets);
        }
    }

    private void RefreshCpuSensors()
    {
        if (_session is not { } session)
            return;

        var status = session.CpuSensors.Status;
        var sample = session.Snapshot.Sample;
        var needsAccess = sample is { CpuTemperature: null } or { CpuPower: null };
        CpuSensorOptions.Visibility = status.Active || status.Authorized || needsAccess ? Visibility.Visible : Visibility.Collapsed;
        if (CpuSensorOptions.Visibility == Visibility.Collapsed)
            return;

        var driver = CpuSensorTask.DriverAvailable;
        CpuSensorButton.IsEnabled = !_revokingSensors && (!status.Active || status.Authorized);
        CpuSensorButton.Content = UiText.T(status.Authorized ? "撤销授权" : "授权采集");
        CpuSensorStatus.Text = UiText.T(status.Error ?? (status.Active
            ? status.Reading switch
            {
                null => "等待 Windows UAC 授权、驱动安装与传感器响应。",
                { Temperature: null, Power: null } => "已授权，当前 CPU 暂无可用传感器读数。",
                _ => "已永久授权 CPU 传感器采集。"
            }
            : status.Authorized ? "CPU 采集授权已保存。" : driver ? "部分 CPU 传感器需要 Windows UAC 授权。"
                : "首次授权会自动安装内置的 PawnIO 驱动。"));
    }

    private async void ToggleCpuSensors(object sender, RoutedEventArgs args)
    {
        if (_session?.CpuSensors is not { } access || _revokingSensors)
            return;

        if (access.Status.Authorized)
        {
            _revokingSensors = true;
            RefreshCpuSensors();
            try { await access.RevokeAsync(); }
            finally { _revokingSensors = false; }
        }
        else access.Start();

        RefreshCpuSensors();
    }

    private void RefreshAlertSwitches(MonitorOptions options)
    {
        WindowsNotifications.IsOn = options.WindowsNotifications;
        WebNotifications.IsOn = options.WebNotifications;
        WebMuteLocal.IsOn = options.WebMuteLocal;
        WebMuteIps.IsOn = options.WebMuteIps;
    }

    private async void ConfirmMutedAddresses(object sender, RoutedEventArgs args)
    {
        var value = WebMutedAddresses.Text;
        if (await Update("webMutedAddresses", value) && WebMutedAddresses.Text == value && _services is { } services)
            WebMutedAddresses.Text = services.Settings.Current.Options.WebMutedAddresses;
    }

    private async void ConfirmAlertNumber(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: string name }) return;
        var input = name switch
        {
            "cpuAlertCelsius" => CpuAlertCelsius,
            "gpuAlertCelsius" => GpuAlertCelsius,
            "diskAlertPercent" => DiskAlertPercent,
            _ => AlertDelaySeconds
        };
        var value = input.Text;
        if (!int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            _message?.Invoke("告警阈值和持续时间必须是整数。");
            return;
        }
        if (await Update(name, number) && input.Text == value)
            input.Text = number.ToString(CultureInfo.InvariantCulture);
    }

    private async void OptionChanged(object sender, RoutedEventArgs args)
    {
        if (_loading || _services is null || sender is not FrameworkElement { Tag: string name } control)
            return;

        if (control is ToggleSwitch toggle)
        {
            var desired = toggle.IsOn;
            if (!await Update(name, desired) && toggle.IsOn == desired)
            {
                _loading = true;
                toggle.IsOn = !desired;
                _loading = false;
            }
        }
        else if (control is ComboBox box && box.SelectedItem is ComboBoxItem { Tag: string value })
        {
            await Update(name, value);
        }
    }

    private async void ConfirmName(object sender, RoutedEventArgs args)
    {
        var name = ComputerName.Text;
        if (await Update("computerName", name) && ComputerName.Text == name)
            ComputerName.Text = name.Trim();
    }

    private async void ConfirmPort(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: string name })
            return;

        var box = name == "webPort" ? WebPort : ApiPort;
        var text = box.Text;
        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            _message?.Invoke("端口必须是 1 至 65535 的整数。");
            return;
        }

        if (await Update(name, port) && box.Text == text)
            box.Text = port.ToString(CultureInfo.InvariantCulture);
    }

    private async void ConfirmPassword(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: string name })
            return;

        var box = name == "webPassword" ? WebPassword : ApiPassword;
        if (box.Password.Length == 0)
        {
            _message?.Invoke("请输入新密码，或使用旁边的清除按钮。");
            return;
        }

        var password = box.Password;
        if (await Update(name, password) && box.Password == password)
            box.Password = "";
    }

    private async void ClearPassword(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: string name } && await Update(name, ""))
            (name == "webPassword" ? WebPassword : ApiPassword).Password = "";
    }

    private async Task<bool> Update<T>(string name, T value)
    {
        if (_services is not { } services)
            return false;

        await _updates.WaitAsync();
        try
        {
            var change = new SettingUpdate(name, JsonSerializer.SerializeToElement(value));
            var saved = await Task.Run(() => services.Settings.Update(change));
            services.Refresh(saved);
            RefreshStatus(services.View());
            _message?.Invoke("已生效");
            return true;
        }
        catch (ArgumentException error)
        {
            _message?.Invoke(error.Message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Runtime.InteropServices.COMException)
        {
            _message?.Invoke("无法应用设置，请检查当前用户的权限。");
        }
        finally
        {
            _updates.Release();
        }
        return false;
    }

    private static void UpdateEndpoint(EndpointStatus endpoint, bool enabled, int port, string? url, TextBlock status, HyperlinkButton link)
    {
        status.Text = UiText.T(!enabled ? "已关闭" : endpoint.Error ?? (endpoint.Running
            ? endpoint.Port != port ? "正在切换端口" : endpoint.Lan ? "运行中 · 所有网络接口" : "运行中 · 仅限此电脑"
            : "正在启动"));
        link.Content = url;
        link.NavigateUri = url is null ? null : new Uri(url);
        link.Visibility = url is null ? Visibility.Collapsed : Visibility.Visible;
        link.IsEnabled = endpoint.Running && (endpoint.Port == port || endpoint.Error is not null);
    }

    private static void Select(ComboBox box, string value) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, value));
}
