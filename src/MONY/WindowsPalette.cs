using Windows.UI;
using Windows.UI.ViewManagement;

namespace MONY;

internal sealed record DashboardAccent(string Base, string Light, string Dark, bool HighContrast = false)
{
    internal static Color ColorFrom(string hex) => Color.FromArgb(255,
        Convert.ToByte(hex.Substring(1, 2), 16),
        Convert.ToByte(hex.Substring(3, 2), 16),
        Convert.ToByte(hex.Substring(5, 2), 16));
}

internal sealed class WindowsPalette : IDisposable
{
    private readonly UISettings _colors = new();
    private readonly AccessibilitySettings _accessibility = new();
    private DashboardAccent _current;

    internal WindowsPalette()
    {
        _current = Read();
        _colors.ColorValuesChanged += ColorsChanged;
    }

    internal DashboardAccent Current => Volatile.Read(ref _current);

    private DashboardAccent Read() => new(
        Hex(UIColorType.Accent), Hex(UIColorType.AccentLight2), Hex(UIColorType.AccentDark2), _accessibility.HighContrast);

    private string Hex(UIColorType type)
    {
        var color = _colors.GetColorValue(type);
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private void ColorsChanged(UISettings sender, object args) => Volatile.Write(ref _current, Read());

    public void Dispose()
    {
        _colors.ColorValuesChanged -= ColorsChanged;
    }
}
