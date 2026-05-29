namespace TrimFetch.Services;

internal static class AppServices
{
    public static AppTrayService Tray { get; } = new();

    public static DownloadNotificationService Notifications { get; } = new();

    public static StartupRegistrationService Startup { get; } = new();
}
