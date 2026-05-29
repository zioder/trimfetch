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
