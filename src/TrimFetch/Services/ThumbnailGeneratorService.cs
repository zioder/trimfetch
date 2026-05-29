using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TrimFetch.Helpers;

namespace TrimFetch.Services;

public sealed class ThumbnailGeneratorService
{
    private static string ThumbnailFolder =>
        Path.Combine(AppDataPaths.StorageRoot, "Thumbnails");

    public async Task<string?> GeneratePosterAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(videoPath))
        {
            return null;
        }

        var thumbnailFolder = ThumbnailFolder;
        Directory.CreateDirectory(thumbnailFolder);
        var outputPath = Path.Combine(thumbnailFolder, $"{HashPath(videoPath)}.ar.jpg");

        if (File.Exists(outputPath) && IsValidImage(outputPath))
        {
            return outputPath;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ExternalToolLocator.FfmpegExecutable,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        foreach (var argument in new[]
        {
            "-hide_banner",
            "-loglevel", "error",
            "-y",
            "-ss", "1",
            "-i", videoPath,
            "-an",
            "-frames:v", "1",
            "-vf", "scale=240:-2:force_original_aspect_ratio=decrease",
            "-q:v", "4",
            outputPath,
        })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Poster ffmpeg failed: {ex.Message}");
            return null;
        }

        if (process.ExitCode != 0 || !IsValidImage(outputPath))
        {
            System.Diagnostics.Debug.WriteLine($"Poster ffmpeg exit {process.ExitCode} for {videoPath}");
            return null;
        }

        return outputPath;
    }

    private static bool IsValidImage(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 256;

    private static string HashPath(string path)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path)));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
