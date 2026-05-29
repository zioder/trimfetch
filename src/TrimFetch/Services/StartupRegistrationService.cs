using Microsoft.Win32;
using Windows.ApplicationModel;

namespace TrimFetch.Services;

public sealed class StartupRegistrationService
{
    private const string RegistryRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RegistryValueName = "TrimFetch";
    private const string StartupTaskId = "TrimFetchStartup";

    public async Task<bool> GetEnabledAsync(PreferencesService preferences) =>
        await preferences.GetRunAtStartupAsync();

    public async Task ApplyPreferenceAsync(bool enabled, PreferencesService preferences)
    {
        await preferences.SetRunAtStartupAsync(enabled);

        if (await TryApplyStartupTaskAsync(enabled))
        {
            return;
        }

        ApplyRegistryRun(enabled);
    }

    public async Task SyncWithPreferenceAsync(PreferencesService preferences)
    {
        var enabled = await preferences.GetRunAtStartupAsync();
        if (await TryApplyStartupTaskAsync(enabled))
        {
            return;
        }

        ApplyRegistryRun(enabled);
    }

    private static async Task<bool> TryApplyStartupTaskAsync(bool enabled)
    {
        try
        {
            var task = await StartupTask.GetAsync(StartupTaskId);
            if (enabled)
            {
                _ = await task.RequestEnableAsync();
            }
            else
            {
                task.Disable();
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyRegistryRun(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, writable: true);
            if (key is null)
            {
                return;
            }

            if (!enabled)
            {
                key.DeleteValue(RegistryValueName, throwOnMissingValue: false);
                return;
            }

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return;
            }

            key.SetValue(RegistryValueName, $"\"{exePath}\"");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Startup registry update failed: {ex.Message}");
        }
    }
}
