using System.Diagnostics;
using System.Text;
using TrimFetch.Helpers;
using TrimFetch.Models;

namespace TrimFetch.Services;

public sealed class MediaDownloaderService
{
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(3);

    public async Task<DownloadResult> DownloadAsync(
        string sourceUrl,
        string destinationFolder,
        DownloadQualityPreset quality = DownloadQualityPreset.Best,
        Action<DownloadProgressUpdate>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        RequireToolsAvailable();

        Directory.CreateDirectory(destinationFolder);
        var startTime = DateTimeOffset.UtcNow.AddSeconds(-2);
        var (format, sort, mergeFormat) = YtDlpDownloadOptions.GetFormatOptions(quality);
        var arguments = new List<string>
        {
            "--no-playlist",
            "--newline",
            "--no-colors",
            "--restrict-filenames",
            "-f", format,
            "--merge-output-format", mergeFormat,
            "-S", sort,
            "--paths", destinationFolder,
            "--output", "%(title).180B [%(id)s].%(ext)s",
            "--print", "after_move:%(filepath)s",
            "--print", "after_move:%(title)s",
            sourceUrl,
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);

        var outputLines = new List<string>();
        try
        {
            await RunYtDlpAsync(ExternalToolLocator.YtDlpExecutable, arguments, outputLines, onProgress, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Download timed out.");
        }

        var filePath = YtDlpOutput.ResolveDownloadedMediaPath(outputLines, destinationFolder, startTime)
            ?? throw new FileNotFoundException("Download finished but no media file was found.");

        var title = outputLines.LastOrDefault(line => !File.Exists(line.Trim()))?.Trim()
            ?? Path.GetFileNameWithoutExtension(filePath);

        return new DownloadResult(filePath, title);
    }

    private static void RequireToolsAvailable()
    {
        ExternalToolLocator.Refresh();
        if (!ExternalToolLocator.IsYtDlpAvailable || !ExternalToolLocator.IsFfmpegAvailable)
        {
            throw new InvalidOperationException("Tools missing");
        }
    }

    private static async Task RunYtDlpAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        List<string> outputLines,
        Action<DownloadProgressUpdate>? onProgress,
        CancellationToken cancellationToken)
    {
        var stdoutLog = new StringBuilder();
        var stderrLog = new StringBuilder();
        var stdoutDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progressState = new YtDlpProgressState();

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
            EnableRaisingEvents = true,
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.Environment["PYTHONUNBUFFERED"] = "1";

        void HandleLine(string? line, StringBuilder log, bool isStdout)
        {
            if (line is null)
            {
                if (isStdout)
                {
                    stdoutDone.TrySetResult();
                }
                else
                {
                    stderrDone.TrySetResult();
                }

                return;
            }

            log.AppendLine(line);
            lock (outputLines)
            {
                outputLines.Add(line);
            }

            if (onProgress is null)
            {
                return;
            }

            if (YtDlpOutput.TryParseDownloadFraction(line, out var downloadFraction))
            {
                progressState.SetDownload(downloadFraction);
                onProgress(new DownloadProgressUpdate(
                    DownloadProgressPhase.Download,
                    progressState.Read().DownloadFraction));
                return;
            }

            if (YtDlpOutput.TryParsePostProcessFraction(line, out var postFraction))
            {
                progressState.SetPostProcess(postFraction);
                onProgress(new DownloadProgressUpdate(
                    DownloadProgressPhase.PostProcess,
                    progressState.Read().PostProcessFraction));
                return;
            }

            if (YtDlpOutput.TryParsePrefetchActivity(line, out var prefetchFraction))
            {
                progressState.SetDownload(Math.Max(progressState.Read().DownloadFraction, prefetchFraction));
                onProgress(new DownloadProgressUpdate(
                    DownloadProgressPhase.Download,
                    progressState.Read().DownloadFraction));
                return;
            }

            if (progressState.Read().DownloadFraction >= 0.2
                && YtDlpOutput.IsPostProcessActivity(line))
            {
                progressState.MarkPostProcessActivity();
                progressState.SetPostProcess(
                    Math.Min(0.95, progressState.Read().PostProcessFraction + 0.05));
                onProgress(new DownloadProgressUpdate(
                    DownloadProgressPhase.PostProcess,
                    progressState.Read().PostProcessFraction));
            }
        }

        process.OutputDataReceived += (_, e) => HandleLine(e.Data, stdoutLog, isStdout: true);
        process.ErrorDataReceived += (_, e) => HandleLine(e.Data, stderrLog, isStdout: false);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var creepTask = RunYtDlpProgressCreepAsync(process, progressState, onProgress, linkedCts.Token);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdoutDone.Task, stderrDone.Task);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            throw;
        }
        finally
        {
            linkedCts.Cancel();
            try
            {
                await creepTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (onProgress is not null)
        {
            var final = progressState.Read();
            if (final.SawPostProcessActivity || final.DownloadBytesComplete)
            {
                onProgress(new DownloadProgressUpdate(DownloadProgressPhase.PostProcess, 1));
            }
        }

        if (process.ExitCode != 0)
        {
            var message = stderrLog.Length > 0 ? stderrLog.ToString() : stdoutLog.ToString();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "Download failed." : message.Trim());
        }
    }

    private static async Task<string> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool throwOnError = true)
    {
        var output = new StringBuilder();
        var errors = new StringBuilder();

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
                errors.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            throw;
        }

        if (throwOnError && process.ExitCode != 0)
        {
            var message = errors.Length > 0 ? errors.ToString() : output.ToString();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "Download failed." : message.Trim());
        }

        return output.ToString();
    }

    /// <summary>
    /// yt-dlp often goes silent after ~30–45% (merge, fragment gap, extract). Keep the bar moving.
    /// </summary>
    private static async Task RunYtDlpProgressCreepAsync(
        Process process,
        YtDlpProgressState state,
        Action<DownloadProgressUpdate>? onProgress,
        CancellationToken cancellationToken)
    {
        if (onProgress is null)
        {
            return;
        }

        while (!process.HasExited)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(150, cancellationToken);

            var snapshot = state.Read();
            if (snapshot.SawPostProcessActivity || snapshot.DownloadBytesComplete)
            {
                var post = Math.Min(0.99, snapshot.PostProcessFraction + 0.035);
                state.SetPostProcess(post);
                onProgress(new DownloadProgressUpdate(DownloadProgressPhase.PostProcess, post));
            }
            else if (snapshot.DownloadFraction > 0.02)
            {
                var download = Math.Min(0.995, snapshot.DownloadFraction + 0.018);
                state.SetDownload(download);
                onProgress(new DownloadProgressUpdate(DownloadProgressPhase.Download, download));
            }
            else
            {
                var download = Math.Min(0.18, snapshot.DownloadFraction + 0.006);
                state.SetDownload(download);
                onProgress(new DownloadProgressUpdate(DownloadProgressPhase.Download, download));
            }
        }
    }

    private static void TryKillProcessTree(Process process)
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
}
