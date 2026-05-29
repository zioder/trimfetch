using TrimFetch.Models;

namespace TrimFetch.Helpers;

internal static class YtDlpDownloadOptions
{
    /// <summary>
    /// Prefer progressive HTTPS MP4 (e.g. Twitter <c>http-*</c>) before HLS fragment downloads.
    /// </summary>
    private const string ProgressiveVideoFormat =
        "best[ext=mp4][vcodec!=none][acodec!=none][protocol^=http]/"
        + "best[ext=mp4][vcodec!=none]/bestvideo*+bestaudio/best";

    public static (string Format, string Sort, string MergeFormat) GetFormatOptions(DownloadQualityPreset preset) =>
        preset switch
        {
            DownloadQualityPreset.Max1080p => (
                "bestvideo[height<=1080]+bestaudio/best[height<=1080][ext=mp4]/best[height<=1080]",
                "res,fps,ext:mp4",
                "mp4"),
            DownloadQualityPreset.Max720p => (
                "bestvideo[height<=720]+bestaudio/best[height<=720][ext=mp4]/best[height<=720]",
                "res,fps,ext:mp4",
                "mp4"),
            DownloadQualityPreset.Smallest => (
                "bestvideo[height<=480]+bestaudio/best[height<=480][ext=mp4]/worst[ext=mp4]",
                "+size,res,ext:mp4",
                "mp4"),
            DownloadQualityPreset.AudioOnly => (
                "ba/b",
                "acodec:aac,ext:m4a",
                "m4a"),
            _ => (
                ProgressiveVideoFormat,
                "res,fps,ext:mp4",
                "mp4"),
        };
}
