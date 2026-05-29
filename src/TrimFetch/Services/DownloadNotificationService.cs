using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using TrimFetch.Helpers;
using TrimFetch.Models;

namespace TrimFetch.Services;

public sealed class DownloadNotificationService
{
    private readonly PreferencesService _preferences = new();
    private bool _registered;

    public void EnsureRegistered()
    {
        if (_registered)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            AppDiagnostic.Log($"Download notifications registration failed: {ex.Message}");
        }
    }

    public async Task ShowDownloadCompletedAsync(DownloadItem item, string? thumbnailPath = null)
    {
        if (!await _preferences.GetShowDownloadNotificationsAsync())
        {
            return;
        }

        EnsureRegistered();

        try
        {
            var headline = $"{item.SourceName} download complete";
            var builder = new AppNotificationBuilder()
                .AddArgument("action", "showApp")
                .AddArgument("itemId", item.Id)
                .AddText(headline)
                .AddText(TrimNotificationText(item.DisplayName))
                .AddText("Copied to clipboard");

            var imagePath = ResolveThumbnailPath(thumbnailPath, item.ThumbnailPath, item.FilePath);
            if (imagePath is not null)
            {
                builder.SetHeroImage(new Uri(imagePath));
                builder.SetAppLogoOverride(new Uri(imagePath), AppNotificationImageCrop.Circle);
            }

            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex)
        {
            AppDiagnostic.Log($"Download notification failed: {ex.Message}");
        }
    }

    private static void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        App.DispatcherQueue?.TryEnqueue(() =>
        {
            if (App.Window is MainWindow window)
            {
                window.ShowFromBackground();
            }
        });
    }

    private static string? ResolveThumbnailPath(string? previewPath, string? itemThumbnailPath, string filePath)
    {
        foreach (var candidate in new[] { itemThumbnailPath, previewPath })
        {
            var uri = ToFileUriIfExists(candidate);
            if (uri is not null)
            {
                return uri;
            }
        }

        return ToFileUriIfExists(filePath);
    }

    private static string? ToFileUriIfExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        return new Uri(Path.GetFullPath(path)).AbsoluteUri;
    }

    private static string TrimNotificationText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return AppBranding.ShortName;
        }

        const int maxLength = 120;
        return value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";
    }
}
