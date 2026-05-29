namespace TrimFetch.Models;

public sealed record DependencyStatus(bool HasYtDlp, bool HasFfmpeg)
{
    public bool IsReady => HasYtDlp && HasFfmpeg;

    public IReadOnlyList<string> MissingTools
    {
        get
        {
            var missing = new List<string>();
            if (!HasYtDlp)
            {
                missing.Add("yt-dlp");
            }

            if (!HasFfmpeg)
            {
                missing.Add("ffmpeg");
            }

            return missing;
        }
    }
}
