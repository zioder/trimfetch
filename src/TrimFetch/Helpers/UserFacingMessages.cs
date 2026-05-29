namespace TrimFetch.Helpers;

internal static class UserFacingMessages
{
    public const string Downloading = "Downloading...";
    public const string Downloaded = "Downloaded and copied.";
    public const string InvalidUrl = "Enter a valid URL.";
    public const string IncompleteUrl = "Paste a full video link.";
    public const string UnsupportedHost = "This site isn't supported yet.";
    public const string AlreadyInLibrary = "Already in your library.";
    public const string DownloadFailed = "Could not download this link.";
    public const string DownloadTimedOut = "Download timed out.";
    public const string SetupNeeded = "Install the video tools first.";
    public const string FfmpegMissing = "Install ffmpeg for trims.";
    public const string InstallingTools = "Installing video tools...";
    public const string InstallingWinget = "Installing Windows Package Manager (winget)...";
    public const string DownloadingWinget = "Downloading App Installer...";
    public const string ApplyingWinget = "Applying App Installer...";
    public const string OpeningAppInstallerStore = "Opening Microsoft Store for App Installer...";
    public const string WingetBootstrapFailed =
        "Could not install winget automatically. Finish installing App Installer in the Microsoft Store, then click Check again.";
    public const string ToolsReady = "Ready to download.";

    public static string FromException(Exception ex)
    {
        var message = ex.Message;
        if (message.Contains("Tools missing", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not found in PATH", StringComparison.OrdinalIgnoreCase))
        {
            return SetupNeeded;
        }

        if (message.Contains("valid URL", StringComparison.OrdinalIgnoreCase))
        {
            return InvalidUrl;
        }

        if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || ex is TimeoutException
            || ex is OperationCanceledException)
        {
            return DownloadTimedOut;
        }

        return DownloadFailed;
    }
}
