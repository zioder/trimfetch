using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TrimFetch.Helpers;

/// <summary>
/// Card chrome for floating overlay sections. Active cards use a light frosted veil over
/// WinUI acrylic; gaps between cards stay desktop-transparent via HWND clip regions.
/// </summary>
internal static class OverlayGlass
{
    private const double DefaultCornerRadius = 8;

    /// <summary>Light frost — acrylic + wallpaper show through; not opaque black.</summary>
    private static readonly SolidColorBrush ActiveCardFill =
        new(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));

    private static readonly SolidColorBrush InactiveFill =
        new(Color.FromArgb(0xF0, 0x1E, 0x21, 0x26));

    private static readonly SolidColorBrush CardStroke =
        new(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));

    public static void ApplyCard(Border border, bool micaActive = false) =>
        ApplySectionCard(border, micaActive);

    public static void ApplySectionCard(Border border, bool micaActive = false)
    {
        var radius = border.CornerRadius.TopLeft > 0 ? border.CornerRadius.TopLeft : DefaultCornerRadius;
        border.CornerRadius = new CornerRadius(radius);
        border.Background = micaActive ? ActiveCardFill : InactiveFill;
        border.BorderBrush = CardStroke;
        border.BorderThickness = new Thickness(1);
        EnsureRoundedClipOnLoad(border);
        ApplyRoundedClip(border, radius);
    }

    public static void ApplyStatusPill(Border border, bool micaActive = false)
    {
        border.CornerRadius = new CornerRadius(4);
        border.Background = micaActive ? ActiveCardFill : InactiveFill;
        border.BorderBrush = CardStroke;
        border.BorderThickness = new Thickness(1);
    }

    private static void EnsureRoundedClipOnLoad(Border border)
    {
        border.Loaded -= Border_Loaded;
        border.Loaded += Border_Loaded;
    }

    private static void Border_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border border)
        {
            ApplyRoundedClip(border, border.CornerRadius.TopLeft);
        }
    }

    private static void ApplyRoundedClip(Border border, double cornerRadius)
    {
        if (border.ActualWidth <= 0 || border.ActualHeight <= 0)
        {
            return;
        }

        var radius = (float)Math.Max(0, Math.Min(cornerRadius, Math.Min(border.ActualWidth, border.ActualHeight) / 2));
        var visual = ElementCompositionPreview.GetElementVisual(border);
        var compositor = visual.Compositor;
        var geometry = compositor.CreateRoundedRectangleGeometry();
        geometry.CornerRadius = new System.Numerics.Vector2(radius, radius);
        geometry.Size = new System.Numerics.Vector2((float)border.ActualWidth, (float)border.ActualHeight);
        visual.Clip = compositor.CreateGeometricClip(geometry);
    }
}
