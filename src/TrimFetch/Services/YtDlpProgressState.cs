namespace TrimFetch.Services;

internal sealed class YtDlpProgressState
{
    private readonly object _gate = new();

    public double DownloadFraction { get; private set; }

    public double PostProcessFraction { get; private set; }

    public bool SawPostProcessActivity { get; private set; }

    public bool DownloadBytesComplete { get; private set; }

    public void SetDownload(double fraction)
    {
        lock (_gate)
        {
            DownloadFraction = Math.Clamp(fraction, 0, 1);
            if (DownloadFraction >= 0.99)
            {
                DownloadBytesComplete = true;
            }
        }
    }

    public void SetPostProcess(double fraction)
    {
        lock (_gate)
        {
            SawPostProcessActivity = true;
            PostProcessFraction = Math.Max(PostProcessFraction, Math.Clamp(fraction, 0, 1));
            if (DownloadFraction < 0.99)
            {
                DownloadFraction = 0.99;
                DownloadBytesComplete = true;
            }
        }
    }

    public void MarkPostProcessActivity()
    {
        lock (_gate)
        {
            SawPostProcessActivity = true;
            if (DownloadFraction >= 0.05)
            {
                DownloadBytesComplete = true;
            }
        }
    }

    public Snapshot Read()
    {
        lock (_gate)
        {
            return new Snapshot(
                DownloadFraction,
                PostProcessFraction,
                SawPostProcessActivity,
                DownloadBytesComplete);
        }
    }

    internal readonly record struct Snapshot(
        double DownloadFraction,
        double PostProcessFraction,
        bool SawPostProcessActivity,
        bool DownloadBytesComplete);
}
