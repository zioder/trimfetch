using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using TrimFetch.Helpers;
using TrimFetch.Models;

namespace TrimFetch.Services;

/// <summary>
/// Fast filmstrip: parallel ffmpeg stills only. Small JPEGs for reference, disk-cached.
/// </summary>
public sealed class ThumbnailStripService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FolderLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly int MaxParallelExtractions = Math.Clamp(Environment.ProcessorCount, 4, 8);

    public const int FrameCount = FilmstripLayout.DefaultFrameCount;

    /// <summary>Matches on-screen strip height for smaller/faster JPEGs.</summary>
    public const int FrameHeight = 48;

    private const int MinFrameBytes = 128;
    private const int JpegQuality = 10;

    private static string RootFolder =>
        Path.Combine(AppDataPaths.StorageRoot, "TimelineFrames");

    public static string BuildScaleFilter(int frameHeight = FrameHeight) =>
        $"scale=-2:{frameHeight}";

    public IReadOnlyList<string> ReadCachedFrames(
        string itemId,
        double durationSeconds,
        VideoDimensions dimensions,
        int frameCount)
    {
        if (durationSeconds <= 0)
        {
            return [];
        }

        frameCount = FilmstripLayout.ClampFrameCount(frameCount);
        var folder = GetCacheFolder(itemId, durationSeconds, dimensions.OrDefault(), frameCount);
        return ReadFrames(folder, frameCount);
    }

    public async Task<IReadOnlyList<string>> GenerateAsync(
        string videoPath,
        string itemId,
        double durationSeconds,
        VideoDimensions dimensions,
        int frameCount,
        IProgress<IReadOnlyList<string>>? progress = null,
        CancellationToken cancellationToken = default)
    {
        videoPath = Path.GetFullPath(videoPath);
        if (!File.Exists(videoPath) || durationSeconds <= 0)
        {
            return [];
        }

        frameCount = FilmstripLayout.ClampFrameCount(frameCount);
        dimensions = dimensions.OrDefault();

        Interlocked.Exchange(ref _loggedExtractFailure, 0);
        ExternalToolLocator.Refresh();
        var folder = GetCacheFolder(itemId, durationSeconds, dimensions, frameCount);
        var scaleFilter = BuildScaleFilter();

        var folderLock = FolderLocks.GetOrAdd(folder, _ => new SemaphoreSlim(1, 1));
        await folderLock.WaitAsync(cancellationToken);
        try
        {
            var existing = ReadFrames(folder, frameCount);
            if (existing.Count == frameCount)
            {
                return existing;
            }

            ClearFrames(folder);
            Directory.CreateDirectory(folder);

            await GenerateParallelAsync(
                videoPath,
                folder,
                durationSeconds,
                frameCount,
                scaleFilter,
                progress,
                cancellationToken);

            return ReadFrames(folder, frameCount);
        }
        finally
        {
            folderLock.Release();
        }
    }

    private string GetCacheFolder(
        string itemId,
        double durationSeconds,
        VideoDimensions dimensions,
        int frameCount) =>
        Path.Combine(RootFolder, itemId, DurationCacheKey(durationSeconds, dimensions, frameCount));

    public static double TimestampForFrameIndex(int index, double durationSeconds, int frameCount)
    {
        if (durationSeconds <= 0 || frameCount <= 0)
        {
            return 0;
        }

        return durationSeconds * (index + 0.5) / frameCount;
    }

    private static string DurationCacheKey(double durationSeconds, VideoDimensions dimensions, int frameCount) =>
        $"{((int)Math.Round(durationSeconds * 10)).ToString(CultureInfo.InvariantCulture)}-{dimensions.Width}x{dimensions.Height}-f{FilmstripLayout.ClampFrameCount(frameCount)}-fast";

    private static async Task GenerateParallelAsync(
        string videoPath,
        string folder,
        double durationSeconds,
        int frameCount,
        string scaleFilter,
        IProgress<IReadOnlyList<string>>? progress,
        CancellationToken cancellationToken)
    {
        var reporter = new FrameProgressReporter(folder, frameCount, progress);
        using var gate = new SemaphoreSlim(MaxParallelExtractions, MaxParallelExtractions);
        var tasks = Enumerable.Range(0, frameCount).Select(async index =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var timestamp = TimestampForFrameIndex(index, durationSeconds, frameCount);
                var outputPath = Path.Combine(folder, $"frame-{(index + 1):D2}.jpg");
                if (await ExtractFrameAsync(videoPath, timestamp, outputPath, scaleFilter, cancellationToken))
                {
                    reporter.ReportIfAdvanced();
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        reporter.ReportFinal();
    }

    private sealed class FrameProgressReporter
    {
        private readonly string _folder;
        private readonly int _frameCount;
        private readonly IProgress<IReadOnlyList<string>>? _progress;
        private readonly object _gate = new();
        private int _lastReportedCount;
        private long _lastReportTicks;

        public FrameProgressReporter(
            string folder,
            int frameCount,
            IProgress<IReadOnlyList<string>>? progress)
        {
            _folder = folder;
            _frameCount = frameCount;
            _progress = progress;
        }

        public void ReportIfAdvanced()
        {
            if (_progress is null)
            {
                return;
            }

            var frames = ReadFrames(_folder, _frameCount);
            if (frames.Count <= _lastReportedCount)
            {
                return;
            }

            var now = Environment.TickCount64;
            lock (_gate)
            {
                frames = ReadFrames(_folder, _frameCount);
                if (frames.Count <= _lastReportedCount)
                {
                    return;
                }

                if (frames.Count < _frameCount && now - _lastReportTicks < 48)
                {
                    return;
                }

                _lastReportedCount = frames.Count;
                _lastReportTicks = now;
            }

            _progress.Report(frames);
        }

        public void ReportFinal()
        {
            if (_progress is null)
            {
                return;
            }

            var frames = ReadFrames(_folder, _frameCount);
            if (frames.Count > _lastReportedCount)
            {
                _progress.Report(frames);
            }
        }
    }

    private static int _loggedExtractFailure;

    private static async Task<bool> ExtractFrameAsync(
        string videoPath,
        double timestampSeconds,
        string outputPath,
        string scaleFilter,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ExternalToolLocator.FfmpegExecutable,
                WorkingDirectory = Path.GetTempPath(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            },
        };

        var timestamp = Math.Max(0, timestampSeconds);
        foreach (var argument in new[]
        {
            "-hide_banner",
            "-loglevel", "error",
            "-threads", "1",
            "-y",
            "-ss", FormatTime(timestamp),
            "-i", videoPath,
            "-an",
            "-frames:v", "1",
            "-vf", scaleFilter,
            "-q:v", JpegQuality.ToString(CultureInfo.InvariantCulture),
            outputPath,
        })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var ok = process.ExitCode == 0 && IsValidFrameFile(outputPath);
            if (!ok && Interlocked.Exchange(ref _loggedExtractFailure, 1) == 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr)
                    ? "(no stderr)"
                    : stderr.Trim();
                if (detail.Length > 240)
                {
                    detail = detail[..240] + "...";
                }

                AppDiagnostic.Log(
                    $"Timeline frame extract failed exit={process.ExitCode} ffmpeg={ExternalToolLocator.FfmpegExecutable} video={videoPath} err={detail}");
            }

            return ok;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _loggedExtractFailure, 1) == 0)
            {
                AppDiagnostic.Log(
                    $"Timeline frame extract exception ffmpeg={ExternalToolLocator.FfmpegExecutable}: {ex.Message}");
            }

            return false;
        }
    }

    private static void ClearFrames(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "frame-*.jpg"))
        {
            TryDelete(file);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static IReadOnlyList<string> ReadFrames(string folder, int frameCount)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.EnumerateFiles(folder, "frame-*.jpg")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Where(IsValidFrameFile)
            .Take(frameCount)
            .ToList();
    }

    private static bool IsValidFrameFile(string path) =>
        File.Exists(path) && new FileInfo(path).Length > MinFrameBytes;

    private static string FormatTime(double seconds) =>
        seconds.ToString("0.###", CultureInfo.InvariantCulture);
}
