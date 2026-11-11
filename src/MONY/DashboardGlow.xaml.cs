using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MONY;

public sealed partial class DashboardGlow : UserControl
{
    public DashboardGlow() => InitializeComponent();

    internal void SetAccent(DashboardAccent accent)
    {
        var color = DashboardAccent.ColorFrom(accent.Base);
        Glow.GradientStops[0].Color = Windows.UI.Color.FromArgb(accent.HighContrast ? (byte)0 : (byte)70, color.R, color.G, color.B);
        Glow.GradientStops[1].Color = Windows.UI.Color.FromArgb(0, color.R, color.G, color.B);
    }

    private void Resize(object sender, SizeChangedEventArgs args)
    {
        var width = args.NewSize.Width;
        var height = args.NewSize.Height;
        if (width <= 0 || height <= 0)
            return;

        // Project the top-left corner onto the gradient axis so the whole upper edge is transparent.
        var fraction = height * height / (width * width + height * height);
        Glow.StartPoint = new(0, height);
        Glow.EndPoint = new(width * fraction, height * fraction);
    }
}
