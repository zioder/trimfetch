using System.Diagnostics;
using System.Text;
using TrimFetch.Helpers;
using TrimFetch.Models;

namespace TrimFetch.Services;

public sealed class DependencySetupService
{
    private readonly WingetBootstrapService _wingetBootstrap = new();

    public const string InstallPrompt =
        "Install ffmpeg and yt-dlp on Windows. TrimFetch uses winget (App Installer). In an elevated terminal: winget install --id yt-dlp.yt-dlp -e && winget install --id Gyan.FFmpeg -e. Verify: ffmpeg -version and yt-dlp --version.";

    private static readonly IReadOnlyDictionary<string, string> WingetPackages = new Dictionary<string, string>
    {
        ["yt-dlp"] = "yt-dlp.yt-dlp",
        ["ffmpeg"] = "Gyan.FFmpeg",
    };

    public async Task<DependencyStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        ExternalToolLocator.Refresh();
        return new DependencyStatus(
            await ToolExistsAsync("yt-dlp", cancellationToken),
            await ToolExistsAsync("ffmpeg", cancellationToken));
    }

    public async Task InstallMissingAsync(
        DependencyStatus status,
        IProgress<DependencySetupProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureWingetForInstallAsync(progress, cancellationToken);

        foreach (var tool in status.MissingTools)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new DependencySetupProgressReport
            {
                ActiveToolId = tool,
                Message = $"Installing {tool}...",
            });

            await InstallToolCoreAsync(tool, cancellationToken);
            await ReportToolCheckAsync(progress, cancellationToken);
        }
    }

    public async Task InstallToolAsync(
        string tool,
        IProgress<DependencySetupProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!WingetPackages.TryGetValue(tool, out _))
        {
            throw new ArgumentException($"Unknown tool: {tool}", nameof(tool));
        }

        await EnsureWingetForInstallAsync(progress, cancellationToken);

        progress?.Report(new DependencySetupProgressReport
        {
            ActiveToolId = tool,
            Message = $"Installing {tool}...",
        });

        await InstallToolCoreAsync(tool, cancellationToken);
        await ReportToolCheckAsync(progress, cancellationToken);
    }

    private async Task EnsureWingetForInstallAsync(
        IProgress<DependencySetupProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        if (await WingetLocator.IsAvailableAsync(cancellationToken))
        {
            progress?.Report(new DependencySetupProgressReport { WingetAvailable = true });
            return;
        }

        var wingetProgress = progress is null
            ? null
            : new Progress<string>(message => progress.Report(new DependencySetupProgressReport
            {
                ActiveToolId = "winget",
                Message = message,
            }));

        await _wingetBootstrap.EnsureWingetAsync(wingetProgress, cancellationToken);
        progress?.Report(new DependencySetupProgressReport
        {
            WingetAvailable = true,
            ActiveToolId = null,
        });
    }

    private async Task ReportToolCheckAsync(
        IProgress<DependencySetupProgressReport>? progress,
        CancellationToken cancellationToken)
    {
        if (progress is null)
        {
            return;
        }

        var status = await CheckAsync(cancellationToken);
        progress.Report(new DependencySetupProgressReport
        {
            ActiveToolId = null,
            YtDlpInstalled = status.HasYtDlp,
            FfmpegInstalled = status.HasFfmpeg,
        });
    }

    private async Task InstallToolCoreAsync(string tool, CancellationToken cancellationToken)
    {
        if (!WingetPackages.TryGetValue(tool, out var packageId))
        {
            throw new ArgumentException($"Unknown tool: {tool}", nameof(tool));
        }

        await RunWingetAsync(packageId, cancellationToken);
        ExternalToolLocator.Invalidate();
        ExternalToolLocator.Refresh();

        if (!await ToolExistsAsync(tool, cancellationToken))
        {
            throw new InvalidOperationException(
                $"{tool} was installed but is not on PATH yet. Restart {AppBranding.ShortName} or sign out and back in, then check again.");
        }
    }

    private static Task<bool> ToolExistsAsync(string tool, CancellationToken cancellationToken) =>
        Task.FromResult(tool switch
        {
            "ffmpeg" => ExternalToolLocator.IsFfmpegAvailable,
            "yt-dlp" => ExternalToolLocator.IsYtDlpAvailable,
            _ => false,
        });

    private static async Task RunWingetAsync(string packageId, CancellationToken cancellationToken)
    {
        var wingetPath = WingetLocator.ResolveExecutablePath()
            ?? throw new InvalidOperationException(UserFacingMessages.WingetBootstrapFailed);

        var result = await RunProcessAsync(
            wingetPath,
            [
                "install",
                "--id", packageId,
                "--exact",
                "--silent",
                "--accept-package-agreements",
                "--accept-source-agreements",
            ],
            cancellationToken,
            throwOnError: false);

        if (result.ExitCode == 0 || IsWingetAlreadyInstalled(result.ExitCode))
        {
            return;
        }

        var message = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
            ? $"Could not install {packageId} with winget."
            : message.Trim());
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool throwOnError = true)
    {
        var output = new StringBuilder();
        var error = new StringBuilder();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

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

        try
        {
            process.Start();
        }
        catch (Exception ex) when (!throwOnError)
        {
            return new ProcessResult(-1, string.Empty, ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);

        var result = new ProcessResult(process.ExitCode, output.ToString(), error.ToString());
        if (throwOnError && process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error)
                ? result.Output.Trim()
                : result.Error.Trim());
        }

        return result;
    }

    private static bool IsWingetAlreadyInstalled(int exitCode) =>
        exitCode is -1978335189 or -1978335212 or -1978335229 or -1978335135;

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
