using System.Diagnostics;
using System.Globalization;
using System.Text;
using TrimFetch.Helpers;
using TrimFetch.Models;

namespace TrimFetch.Services;

public sealed class TrimExportService
{
    public async Task<string> ExportTrimAsync(
        string sourcePath,
        TrimSelection selection,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source video was not found.", sourcePath);
        }

        if (selection.DurationSeconds < 0.25)
        {
            throw new InvalidOperationException("Choose a longer trim range.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        var copyArguments = BuildArguments(sourcePath, selection, outputPath, streamCopy: true);
        var exitCode = await RunProcessAsync(ExternalToolLocator.FfmpegExecutable, copyArguments, cancellationToken);
        if (exitCode == 0 && IsValidOutput(outputPath))
        {
            return outputPath;
        }

        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        var encodeArguments = BuildArguments(sourcePath, selection, outputPath, streamCopy: false);
        exitCode = await RunProcessAsync(ExternalToolLocator.FfmpegExecutable, encodeArguments, cancellationToken);
        if (exitCode != 0 || !IsValidOutput(outputPath))
        {
            throw new InvalidOperationException("Trim export failed.");
        }

        return outputPath;
    }

    public string CreateDefaultTrimPath(string sourcePath, TrimSelection selection)
    {
        var folder = Path.GetDirectoryName(sourcePath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var start = Math.Round(selection.StartSeconds);
        var end = Math.Round(selection.EndSeconds);
        return Path.Combine(folder, $"{name} trim {start}-{end}s.mp4");
    }

    public string CreateTemporaryTrimPath(string sourcePath)
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppBranding.StorageFolderName,
            "TrimExports");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"{Guid.NewGuid():N}.mp4");
    }

    /// <summary>
    /// Exports a trim clip beside the source video (same folder as Save) for reliable clipboard copy.
    /// </summary>
    public string CreateClipboardTrimPath(string sourcePath)
    {
        var folder = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return CreateTemporaryTrimPath(sourcePath);
        }

        Directory.CreateDirectory(folder);
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        return Path.Combine(folder, $"{baseName} clip {Guid.NewGuid():N}.mp4");
    }

    public string CreateTemporaryGifTrimPath()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppBranding.StorageFolderName,
            "TrimExports");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"{Guid.NewGuid():N}.gif");
    }

    public async Task<string> ExportGifTrimAsync(
        string sourcePath,
        TrimSelection selection,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source video was not found.", sourcePath);
        }

        if (selection.DurationSeconds < 0.25)
        {
            throw new InvalidOperationException("Choose a longer trim range.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        var tempSegmentPath = Path.Combine(
            Path.GetDirectoryName(outputPath) ?? ".",
            $"{Guid.NewGuid():N}.mp4");

        try
        {
            await ExtractGifSourceSegmentAsync(sourcePath, selection, tempSegmentPath, cancellationToken);

            var profile = GifEncodeProfile.ForDuration(selection.DurationSeconds);
            var arguments = BuildQualityGifArguments(tempSegmentPath, outputPath, profile);
            var exitCode = await RunProcessAsync(ExternalToolLocator.FfmpegExecutable, arguments, cancellationToken);
            if (exitCode != 0 || !IsValidOutput(outputPath))
            {
                throw new InvalidOperationException("GIF trim export failed.");
            }

            return outputPath;
        }
        finally
        {
            TryDelete(tempSegmentPath);
        }
    }

    /// <summary>
    /// Isolates the trim segment in a short MP4 so GIF encoding only decodes that clip,
    /// not the full source file (major speed win on long downloads).
    /// </summary>
    private static async Task ExtractGifSourceSegmentAsync(
        string sourcePath,
        TrimSelection selection,
        string tempSegmentPath,
        CancellationToken cancellationToken)
    {
        if (File.Exists(tempSegmentPath))
        {
            File.Delete(tempSegmentPath);
        }

        var copyArguments = BuildArguments(sourcePath, selection, tempSegmentPath, streamCopy: true);
        var exitCode = await RunProcessAsync(ExternalToolLocator.FfmpegExecutable, copyArguments, cancellationToken);
        if (exitCode == 0 && IsValidOutput(tempSegmentPath))
        {
            return;
        }

        TryDelete(tempSegmentPath);

        var encodeArguments = BuildFastSegmentArguments(sourcePath, selection, tempSegmentPath);
        exitCode = await RunProcessAsync(ExternalToolLocator.FfmpegExecutable, encodeArguments, cancellationToken);
        if (exitCode != 0 || !IsValidOutput(tempSegmentPath))
        {
            throw new InvalidOperationException("GIF trim export failed.");
        }
    }

    private static IReadOnlyList<string> BuildFastSegmentArguments(
        string sourcePath,
        TrimSelection selection,
        string outputPath) =>
    [
        "-y",
        "-ss", FormatTime(selection.StartSeconds),
        "-i", sourcePath,
        "-t", FormatTime(selection.DurationSeconds),
        "-map", "0:v:0",
        "-an",
        "-sn",
        "-dn",
        "-c:v", "libx264",
        "-preset", "ultrafast",
        "-crf", "28",
        "-pix_fmt", "yuv420p",
        "-movflags", "+faststart",
        outputPath,
    ];

    private static IReadOnlyList<string> BuildQualityGifArguments(
        string sourcePath,
        string outputPath,
        GifEncodeProfile profile)
    {
        var args = new List<string> { "-y" };
        args.AddRange(GifInputPrefix(sourcePath));
        args.AddRange(
        [
            "-vf", profile.BuildVideoFilter(),
            "-loop", "0",
            outputPath,
        ]);
        return args;
    }

    private static IEnumerable<string> GifInputPrefix(string sourcePath)
    {
        yield return "-hide_banner";
        yield return "-loglevel";
        yield return "error";
        yield return "-i";
        yield return sourcePath;
        yield return "-an";
        yield return "-sn";
        yield return "-dn";
        yield return "-threads";
        yield return "0";
    }

    private sealed class GifEncodeProfile
    {
        private const int PaletteColors = 128;

        public required int Fps { get; init; }

        public required int MaxWidth { get; init; }

        public string BuildVideoFilter() =>
            $"fps={Fps},scale='min({MaxWidth},iw)':-2:flags=fast_bilinear,split[s0][s1];"
            + $"[s0]palettegen=max_colors={PaletteColors}:stats_mode=single[p];"
            + $"[s1][p]paletteuse=dither=bayer:bayer_scale=3";

        public static GifEncodeProfile ForDuration(double durationSeconds)
        {
            if (durationSeconds <= 10)
            {
                return new GifEncodeProfile
                {
                    Fps = 15,
                    MaxWidth = 480,
                };
            }

            if (durationSeconds <= 20)
            {
                return new GifEncodeProfile
                {
                    Fps = 12,
                    MaxWidth = 420,
                };
            }

            if (durationSeconds <= 40)
            {
                return new GifEncodeProfile
                {
                    Fps = 10,
                    MaxWidth = 380,
                };
            }

            return new GifEncodeProfile
            {
                Fps = 8,
                MaxWidth = 320,
            };
        }
    }

    public async Task<string> ExportAudioTrimAsync(
        string sourcePath,
        TrimSelection selection,
        string outputPath,
        TrimAudioFormat format,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source media was not found.", sourcePath);
        }

        if (selection.DurationSeconds < 0.25)
        {
            throw new InvalidOperationException("Choose a longer trim range.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        var arguments = BuildAudioArguments(sourcePath, selection, outputPath, format);
        var exitCode = await RunProcessAsync(ExternalToolLocator.FfmpegExecutable, arguments, cancellationToken);
        if (exitCode != 0 || !IsValidOutput(outputPath))
        {
            throw new InvalidOperationException("Audio trim export failed.");
        }

        return outputPath;
    }

    public string CreateDefaultAudioTrimPath(
        string sourcePath,
        TrimSelection selection,
        TrimAudioFormat format)
    {
        var folder = Path.GetDirectoryName(sourcePath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var start = Math.Round(selection.StartSeconds);
        var end = Math.Round(selection.EndSeconds);
        var extension = GetAudioExtension(format);
        return Path.Combine(folder, $"{name} trim {start}-{end}s{extension}");
    }

    public string CreateTemporaryAudioTrimPath(TrimAudioFormat format)
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppBranding.StorageFolderName,
            "TrimExports");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"{Guid.NewGuid():N}{GetAudioExtension(format)}");
    }

    public string CreateClipboardAudioTrimPath(string sourcePath, TrimAudioFormat format)
    {
        var folder = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return CreateTemporaryAudioTrimPath(format);
        }

        Directory.CreateDirectory(folder);
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        return Path.Combine(folder, $"{baseName} clip {Guid.NewGuid():N}{GetAudioExtension(format)}");
    }

    private static string GetAudioExtension(TrimAudioFormat format) =>
        format == TrimAudioFormat.Wav ? ".wav" : ".mp3";

    private static IReadOnlyList<string> BuildAudioArguments(
        string sourcePath,
        TrimSelection selection,
        string outputPath,
        TrimAudioFormat format)
    {
        if (format == TrimAudioFormat.Wav)
        {
            return
            [
                "-y",
                "-ss", FormatTime(selection.StartSeconds),
                "-i", sourcePath,
                "-t", FormatTime(selection.DurationSeconds),
                "-vn",
                "-acodec", "pcm_s16le",
                outputPath,
            ];
        }

        return
        [
            "-y",
            "-ss", FormatTime(selection.StartSeconds),
            "-i", sourcePath,
            "-t", FormatTime(selection.DurationSeconds),
            "-vn",
            "-acodec", "libmp3lame",
            "-q:a", "2",
            outputPath,
        ];
    }

    private static IReadOnlyList<string> BuildArguments(
        string sourcePath,
        TrimSelection selection,
        string outputPath,
        bool streamCopy)
    {
        if (streamCopy)
        {
            return
            [
                "-y",
                "-ss", FormatTime(selection.StartSeconds),
                "-i", sourcePath,
                "-t", FormatTime(selection.DurationSeconds),
                "-map", "0:v:0",
                "-map", "0:a?",
                "-c", "copy",
                "-avoid_negative_ts", "make_zero",
                "-movflags", "+faststart",
                outputPath,
            ];
        }

        return
        [
            "-y",
            "-ss", FormatTime(selection.StartSeconds),
            "-i", sourcePath,
            "-t", FormatTime(selection.DurationSeconds),
            "-map", "0:v:0",
            "-map", "0:a?",
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-crf", "20",
            "-c:a", "aac",
            "-b:a", "128k",
            "-movflags", "+faststart",
            outputPath,
        ];
    }

    private static bool IsValidOutput(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 1024;

    private static string FormatTime(double seconds) =>
        seconds.ToString("0.###", CultureInfo.InvariantCulture);

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

    private static async Task<int> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var errors = new StringBuilder();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                errors.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }
}
