using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TrimFetch.Helpers;

/// <summary>
/// A custom acrylic backdrop tuned to be lighter and more transparent than the system default,
/// so the content behind the floating overlay clearly shows through (the default dark acrylic —
/// and Mica, which only samples the wallpaper — read too dark over other windows).
///
/// Tune the four constants below to taste:
///   - <see cref="LuminosityOpacity"/> is the strongest lever — lower shows more background.
///   - <see cref="TintOpacity"/> / <see cref="TintColor"/> set how dark the glass tint is
///     (kept dark enough for the overlay's white text to stay readable).
/// </summary>
internal sealed partial class OverlayAcrylicBackdrop : SystemBackdrop
{
    private static readonly Color TintColor = Color.FromArgb(0xFF, 0x1C, 0x1E, 0x23);
    private const float TintOpacity = 0.30f;
    private const float LuminosityOpacity = 0.55f;
    private static readonly Color FallbackColor = Color.FromArgb(0xFF, 0x1E, 0x21, 0x26);

    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _configuration;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);

        // The overlay is always shown active and forces the dark theme, so a fixed config is
        // sufficient (no light-theme / inactive-state handling needed).
        _configuration = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = SystemBackdropTheme.Dark,
        };

        _controller = new DesktopAcrylicController
        {
            TintColor = TintColor,
            TintOpacity = TintOpacity,
            LuminosityOpacity = LuminosityOpacity,
            FallbackColor = FallbackColor,
        };

        _controller.AddSystemBackdropTarget(connectedTarget);
        _controller.SetSystemBackdropConfiguration(_configuration);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);

        _controller?.RemoveAllSystemBackdropTargets();
        _controller?.Dispose();
        _controller = null;
        _configuration = null;
    }
}
