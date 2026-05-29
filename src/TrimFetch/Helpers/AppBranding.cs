namespace TrimFetch.Helpers;

internal static class AppBranding
{
    public const string ShortName = "TrimFetch";

    public const string FullName = "TrimFetch Media Downloader";

    public const string StorageFolderName = "TrimFetch";

    public const string HttpUserAgent = "TrimFetchMediaDownloader";

    public const string DefaultUpdatePackageFileName = "TrimFetchSetup-update.exe";

    /// <summary>Packaged app icon (PNG) for in-app UI (Settings about section).</summary>
    public const string AppIconPngUri = "ms-appx:///Assets/TrimFetch.png";

    /// <summary>Window title-bar / taskbar icon path relative to the package root.</summary>
    public const string AppIconIcoPath = "Assets/AppIcon.ico";

    /// <summary>System tray icon (ICO) for H.NotifyIcon.</summary>
    public const string AppIconTraySource = "ms-appx:///Assets/AppIcon.ico";
}
