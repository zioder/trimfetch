using System.Diagnostics;
using System.Net.Http;
using TrimFetch.Helpers;

namespace TrimFetch.Services;

internal sealed class WingetBootstrapService
{
    private const long MaxBundleBytes = 250L * 1024 * 1024;

    public async Task EnsureWingetAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (await WingetLocator.IsAvailableAsync(cancellationToken))
        {
            return;
        }

        progress?.Report(UserFacingMessages.InstallingWinget);

        try
        {
            await InstallAppInstallerFromDownloadAsync(progress, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppDiagnostic.Log($"App Installer download install failed: {ex.Message}");
            progress?.Report(UserFacingMessages.OpeningAppInstallerStore);
            TryOpenAppInstallerInStore();
            throw new InvalidOperationException(UserFacingMessages.WingetBootstrapFailed, ex);
        }

        ExternalToolLocator.RefreshProcessPath();

        if (!await WingetLocator.IsAvailableAsync(cancellationToken))
        {
            TryOpenAppInstallerInStore();
            throw new InvalidOperationException(UserFacingMessages.WingetBootstrapFailed);
        }
    }

    private static async Task InstallAppInstallerFromDownloadAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var setupDir = Path.Combine(Path.GetTempPath(), AppBranding.StorageFolderName, "setup");
        Directory.CreateDirectory(setupDir);
        var bundlePath = Path.Combine(setupDir, "Microsoft.DesktopAppInstaller.msixbundle");

        progress?.Report(UserFacingMessages.DownloadingWinget);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd(AppBranding.HttpUserAgent);
        http.Timeout = TimeSpan.FromMinutes(10);

        using var response = await http.GetAsync(
            WingetLocator.AppInstallerDownloadUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is > MaxBundleBytes)
        {
            throw new InvalidOperationException("App Installer download exceeded the expected size.");
        }

        await using (var network = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var file = File.Create(bundlePath))
        {
            await network.CopyToAsync(file, cancellationToken);
        }

        if (new FileInfo(bundlePath).Length > MaxBundleBytes)
        {
            throw new InvalidOperationException("App Installer download exceeded the expected size.");
        }

        progress?.Report(UserFacingMessages.ApplyingWinget);

        var escapedPath = bundlePath.Replace("'", "''");
        var command =
            $"Add-AppxPackage -ForceUpdateFromAnyVersion -ForceApplicationShutdown -Path '{escapedPath}'";

        var result = await RunPowerShellAsync(command, cancellationToken);
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? "Add-AppxPackage failed for App Installer."
                : detail.Trim());
        }
    }

    private static void TryOpenAppInstallerInStore()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = WingetLocator.AppInstallerStoreUri,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppDiagnostic.Log($"Could not open Microsoft Store for App Installer: {ex.Message}");
        }
    }

    private static async Task<ProcessResult> RunPowerShellAsync(string command, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(command);

        var output = new System.Text.StringBuilder();
        var error = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                output.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                error.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, output.ToString(), error.ToString());
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
