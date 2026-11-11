using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MONY;

public sealed partial class DashboardHeader : UserControl
{
    private TargetChoice[] _choices = [];

    public DashboardHeader() => InitializeComponent();

    public event RoutedEventHandler? PauseRequested;
    public event RoutedEventHandler? SettingsRequested;
    public event EventHandler<string>? TargetSelected;

    private void RequestPause(object sender, RoutedEventArgs args) => PauseRequested?.Invoke(this, args);
    private void RequestSettings(object sender, RoutedEventArgs args) => SettingsRequested?.Invoke(this, args);
    internal void Display(DashboardSnapshot snapshot)
    {
        if (!_choices.SequenceEqual(snapshot.Targets))
        {
            _choices = snapshot.Targets;
            TargetMenu.Items.Clear();
            foreach (var choice in _choices)
            {
                var item = new MenuFlyoutItem { Text = choice.Label };
                item.Click += (_, _) => TargetSelected?.Invoke(this, choice.Id);
                TargetMenu.Items.Add(item);
            }
        }
        TargetLabel.Text = _choices.FirstOrDefault(choice => choice.Id == snapshot.TargetId)?.Label ?? "";
        ToolTipService.SetToolTip(Identity, TargetLabel.Text);
        Identity.IsEnabled = _choices.Length > 1;
        TargetArrow.Visibility = Identity.IsEnabled ? Visibility.Visible : Visibility.Collapsed;

        var status = snapshot.Paused ? UiText.T("已暂停") : snapshot.TargetId != RemoteMonitors.Local
            ? snapshot.Status : "";
        Status.Text = status;
        Status.Visibility = status.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        SampleTime.Text = snapshot.SampleTime;
        SampleTime.Visibility = snapshot.SampleTime.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        PauseGlyph.Glyph = snapshot.Paused ? "\uE768" : "\uE769";
        var name = UiText.T(snapshot.Paused ? "继续" : "暂停");
        AutomationProperties.SetName(Pause, name);
        ToolTipService.SetToolTip(Pause, name);
    }
}
