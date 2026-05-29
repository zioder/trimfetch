using System.Diagnostics;
using System.Text;
using TrimFetch.Helpers;

namespace TrimFetch.Services;

public sealed class VideoThumbnailFetchService
{
    private readonly string _previewFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppBranding.StorageFolderName,
        "PreviewThumbnails");

    public async Task<string?> FetchThumbnailAsync(string sourceUrl, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_previewFolder);
        var stagingDir = Path.Combine(_previewFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDir);

        var outputTemplate = Path.Combine(stagingDir, "%(id)s");
        var arguments = new[]
        {
            "--no-playlist",
            "--write-thumbnail",
            "--skip-download",
            "--convert-thumbnails", "jpg",
            "-o", outputTemplate,
            sourceUrl,
        };

        try
        {
            ExternalToolLocator.Refresh();
            var exitCode = await RunProcessAsync(ExternalToolLocator.YtDlpExecutable, arguments, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (exitCode != 0)
            {
                return null;
            }

            var thumbnail = Directory
                .EnumerateFiles(stagingDir, "*.jpg", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            return thumbnail;
        }
        catch (OperationCanceledException)
        {
            TryDeleteDirectory(stagingDir);
            throw;
        }
        catch
        {
            TryDeleteDirectory(stagingDir);
            return null;
        }
    }

    public void TryDeletePreview(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)
                && directory.StartsWith(_previewFolder, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(directory))
            {
                TryDeleteDirectory(directory);
            }
        }
        catch
        {
        }
    }

    private static async Task<int> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
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
            EnableRaisingEvents = true,
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        var errors = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                errors.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }

        return process.ExitCode;
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
