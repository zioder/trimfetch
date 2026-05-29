namespace TrimFetch.Helpers;

public static class FilmstripLayout
{
    public const double FilmstripHeight = 48;
    public const double TrackInset = 4;
    public const double MinColumnWidth = 20;
    public const double MaxColumnWidth = 80;
    public const int MinFrameCount = 6;
    public const int MaxFrameCount = 64;
    public const int DefaultFrameCount = 18;
    public const double DefaultAspectRatio = 16.0 / 9.0;

    /// <summary>Typical filmstrip width (680 content minus trim margins/inset) for prefetch before layout.</summary>
    public const double DefaultFilmstripWidth = 640;

    public static int ClampFrameCount(int frameCount) =>
        Math.Clamp(frameCount, MinFrameCount, MaxFrameCount);

    public static double NormalizeAspect(double aspectRatio) =>
        double.IsFinite(aspectRatio) && aspectRatio > 0
            ? aspectRatio
            : DefaultAspectRatio;

    public static int CalculateFrameCount(double availableWidth, double aspectRatio)
    {
        if (availableWidth <= 0)
        {
            return DefaultFrameCount;
        }

        var aspect = NormalizeAspect(aspectRatio);
        var idealColumnWidth = Math.Clamp(FilmstripHeight * aspect, MinColumnWidth, MaxColumnWidth);
        var count = (int)Math.Ceiling(availableWidth / idealColumnWidth);
        return ClampFrameCount(count);
    }

    public static double CalculateColumnWidth(double availableWidth, int frameCount)
    {
        if (frameCount <= 0 || availableWidth <= 0)
        {
            return IdealColumnWidth(DefaultAspectRatio);
        }

        return availableWidth / frameCount;
    }

    public static double IdealColumnWidth(double aspectRatio) =>
        Math.Clamp(FilmstripHeight * NormalizeAspect(aspectRatio), MinColumnWidth, MaxColumnWidth);
}
