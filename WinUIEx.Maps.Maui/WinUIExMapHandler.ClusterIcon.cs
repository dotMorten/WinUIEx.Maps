using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace WinUIEx.Maps.Maui;

public partial class WinUIExMapHandler
{
    private static ImageIcon CreateClusterIcon(int count)
    {
        string text = count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        float diameter = 28 + Math.Max(0, text.Length - 2) * 6;
        float size = diameter + 12;
        float center = size / 2;
        float radius = diameter / 2;
        bool highContrast = new AccessibilitySettings().HighContrast;
        var settings = new UISettings();
        Color fill = highContrast ? settings.GetColorValue(UIColorType.Background)
            : Color.FromArgb(255, 246, 119, 134);
        Color foreground = highContrast ? settings.GetColorValue(UIColorType.Foreground)
            : Color.FromArgb(255, 52, 28, 35);
        Color outline = highContrast ? foreground : Color.FromArgb(220, 163, 62, 80);

        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), size, size, 192);
        using var format = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = text.Length > 4 ? 12 : 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };
        using (var drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Microsoft.UI.Colors.Transparent);
            if (!highContrast)
                for (int ring = 6; ring > 0; ring--)
                    drawing.FillCircle(center, center + 1.5f, radius + ring * 0.6f,
                        Color.FromArgb(8, 24, 12, 18));
            drawing.FillCircle(center, center, radius, fill);
            drawing.DrawCircle(center, center, radius - 0.5f, outline, 1);
            if (!highContrast)
                drawing.DrawCircle(center, center, radius - 1.5f,
                    Color.FromArgb(105, 255, 225, 230), 0.75f);
            drawing.DrawText(text, new Rect(0, 0, size, size), foreground, format);
        }
        var bitmap = new WriteableBitmap((int)(size * 2), (int)(size * 2));
        target.GetPixelBytes(bitmap.PixelBuffer);
        bitmap.Invalidate();
        return new ImageIcon { Source = bitmap, Width = size, Height = size };
    }
}
