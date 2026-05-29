namespace TrimFetch.Services;

public enum DownloadProgressPhase
{
    Download,
    PostProcess,
}

public readonly record struct DownloadProgressUpdate(DownloadProgressPhase Phase, double Fraction);
