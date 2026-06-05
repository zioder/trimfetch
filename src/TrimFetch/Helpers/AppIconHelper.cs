using Microsoft.UI.Xaml.Media.Imaging;

namespace TrimFetch.Helpers;

internal static class AppIconHelper
{
    /// <summary>
    /// Full path to a loose asset next to the app (unpackaged install) or the original relative path for MSIX.
    /// </summary>
    public static string ResolveAssetPath(string relativeAssetPath)
    {
        var normalized = relativeAssetPath.Replace('/', Path.DirectorySeparatorChar);
        var candidate = Path.Combine(AppContext.BaseDirectory, normalized);
        return File.Exists(candidate) ? candidate : relativeAssetPath;
    }

    public static Uri CreateAssetUri(string relativeAssetPath)
    {
        var fullPath = ResolveAssetPath(relativeAssetPath);
        if (File.Exists(fullPath))
        {
            return new Uri(fullPath);
        }

        var packaged = relativeAssetPath.Replace('\\', '/');
        if (!packaged.StartsWith("ms-appx:", StringComparison.OrdinalIgnoreCase))
        {
            packaged = $"ms-appx:///{packaged}";
        }

        return new Uri(packaged);
    }

    public static BitmapImage CreateAssetImage(string relativeAssetPath) =>
        new(CreateAssetUri(relativeAssetPath));

    /// <summary>
    /// Loads <see cref="AppBranding.AppIconIcoPath"/> as a <see cref="System.Drawing.Icon"/>
    /// straight from the resolved file path. H.NotifyIcon's <c>IconSource</c> pipeline routes
    /// through <c>StorageFile.GetFileFromApplicationUriAsync</c>, which only accepts
    /// <c>ms-appx://</c>/<c>ms-appdata://</c> URIs and throws on the <c>file://</c> URI we
    /// resolve to — so the tray sets its icon from this directly instead.
    /// </summary>
    public static System.Drawing.Icon? LoadAppIcon()
    {
        try
        {
            var path = ResolveAssetPath(AppBranding.AppIconIcoPath);
            return File.Exists(path) ? new System.Drawing.Icon(path) : null;
        }
        catch
        {
            return null;
        }
    }

    public static void TrySetWindowIcon(Microsoft.UI.Windowing.AppWindow appWindow)
    {
        try
        {
            appWindow.SetIcon(ResolveAssetPath(AppBranding.AppIconIcoPath));
        }
        catch
        {
            // Ignore missing icon on unsupported hosts.
        }
    }
}
