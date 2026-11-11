using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MONY;

internal static class UiText
{
    internal static readonly string Source = ReadCatalog();
    private static readonly Dictionary<string, string> Strings =
        JsonSerializer.Deserialize<Dictionary<string, string>>(Source)!;
    private static readonly Dictionary<string, string> Original =
        Strings.GroupBy(pair => pair.Value).ToDictionary(group => group.Key, group => group.First().Key);
    private static readonly string[] Fragments =
    [
        "更新于 ", " 个逻辑处理器", "目标 API 返回 HTTP ", "磁盘 ", "封装温度", "封装功耗", "频率",
        "温度", "功耗", "专用显存", "共享显存", "读取 ", "写入 ", "已用 ", "剩余 ", "可用 ", "总计 "
    ];
    private static readonly ConditionalWeakTable<FrameworkElement, List<Action<bool>>> StaticText = new();

    internal static bool DesktopEnglish { get; private set; }

    private static string ReadCatalog()
    {
        using var stream = typeof(UiText).Assembly.GetManifestResourceStream("MONY.Web.strings.json")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static bool IsEnglish(string preference, string? culture)
    {
        if (preference == "en") return true;
        if (preference == "zh-CN") return false;
        var name = culture?.Split(',', ';')[0].Trim() ?? "";
        return !name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("zh-SG", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("zh-Hans-", StringComparison.OrdinalIgnoreCase);
    }

    internal static string T(string value) => Translate(value, DesktopEnglish);

    internal static string Translate(string value, bool english)
    {
        if (!english) return value;
        if (Strings.TryGetValue(value, out var text)) return text;
        if (value.StartsWith("无法监听 ", StringComparison.Ordinal))
        {
            value = System.Text.RegularExpressions.Regex.Replace(value, @"^无法监听 (\d+)，仍使用端口 (\d+)。$", "Could not listen on port $1. Still using port $2.");
            value = System.Text.RegularExpressions.Regex.Replace(value, @"^无法监听 (\d+) 端口。请检查端口占用后重试。$", "Could not listen on port $1. Check whether it is in use.");
        }
        foreach (var part in Fragments)
            value = value.Replace(part, Strings[part], StringComparison.Ordinal);
        return value;
    }

    internal static string Count(int value) => DesktopEnglish ? $"{value}" : $"{value} 个";

    internal static void ApplyDesktop(FrameworkElement root, string preference)
    {
        DesktopEnglish = IsEnglish(preference, CultureInfo.CurrentUICulture.Name);
        Apply(root, DesktopEnglish);
    }

    private static void Apply(FrameworkElement element, bool english)
    {
        foreach (var update in StaticText.GetValue(element, Capture))
            update(english);

        switch (element)
        {
            case Panel panel:
                foreach (var child in panel.Children.OfType<FrameworkElement>()) Apply(child, english);
                break;
            case Border { Child: FrameworkElement child }:
                Apply(child, english);
                break;
            case UserControl { Content: FrameworkElement userContent }:
                Apply(userContent, english);
                break;
            case ScrollViewer { Content: FrameworkElement scrollContent }:
                Apply(scrollContent, english);
                break;
            case ContentControl { Content: FrameworkElement content }:
                Apply(content, english);
                break;
            case ComboBox combo:
                var index = combo.SelectedIndex;
                foreach (var item in combo.Items.OfType<FrameworkElement>()) Apply(item, english);
                if (index >= 0)
                {
                    combo.SelectedIndex = -1;
                    combo.SelectedIndex = index;
                }
                break;
        }
    }

    private static List<Action<bool>> Capture(FrameworkElement element)
    {
        var updates = new List<Action<bool>>();
        void Text(string? original, Action<string> set)
        {
            if (original is null) return;
            if (!Strings.ContainsKey(original) && Original.TryGetValue(original, out var chinese))
                original = chinese;
            if (Strings.TryGetValue(original, out var english))
                updates.Add(useEnglish => set(useEnglish ? english : original));
        }

        if (element is TextBlock block) Text(block.Text, value => block.Text = value);
        if (element is TextBox box) Text(box.PlaceholderText, value => box.PlaceholderText = value);
        if (element is PasswordBox password) Text(password.PlaceholderText, value => password.PlaceholderText = value);
        if (element is ContentControl { Content: string label } control) Text(label, value => control.Content = value);
        if (ToolTipService.GetToolTip(element) is string tooltip) Text(tooltip, value => ToolTipService.SetToolTip(element, value));
        Text(AutomationProperties.GetName(element), value => AutomationProperties.SetName(element, value));
        return updates;
    }

    internal static DashboardSnapshot Dashboard(DashboardSnapshot snapshot, bool english)
    {
        if (!english) return snapshot;
        string T(string value) => Translate(value, english);
        return snapshot with
        {
            Status = T(snapshot.Status),
            SampleTime = T(snapshot.SampleTime),
            Metrics = snapshot.Metrics.Select(metric => metric with
            {
                Title = T(metric.Title),
                Detail = metric.Id is "cpu" or "memory" or "network" ? T(metric.Detail) : metric.Detail,
                Telemetry = metric.Telemetry is null ? null : T(metric.Telemetry),
                TelemetryLabels = metric.TelemetryLabels?.Select(label => label with { Text = T(label.Text) }).ToArray()
            }).ToArray(),
            Volumes = snapshot.Volumes.Select(volume => volume with
            {
                Name = volume.Name == "未命名" ? T(volume.Name) : volume.Name,
                Capacity = T(volume.Capacity),
                Usage = T(volume.Usage)
            }).ToArray()
        };
    }
}
