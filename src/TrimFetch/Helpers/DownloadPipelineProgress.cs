namespace TrimFetch.Helpers;

/// <summary>
/// Maps pre-reveal work onto 0–<see cref="PreRevealMax"/>; only <c>1.0</c> is reported at trim reveal.
/// </summary>
public static class DownloadPipelineProgress
{
    /// <summary>Bar may approach this during download, merge, and hidden trim prep — never 1.0 until reveal.</summary>
    public const double PreRevealMax = 0.99;

    private const double DownloadShare = 0.62;
    private const double PostProcessShare = 0.16;
    private const double TrimPrepShare = 0.21;

    private const double DownloadEnd = DownloadShare * PreRevealMax;
    /// <summary>Bar stops here during hidden trim prep; only the reveal sequence may go higher.</summary>
    public const double PostProcessEnd = DownloadEnd + PostProcessShare * PreRevealMax;

    private const double TrimPrepEnd = PreRevealMax;

    public static double FromDownloadHighWater(double segmentHighWater) =>
        Math.Clamp(segmentHighWater, 0, 1) * DownloadEnd;

    public static double FromPostProcessPhase(double postProcessFraction) =>
        DownloadEnd + Math.Clamp(postProcessFraction, 0, 1) * (PostProcessEnd - DownloadEnd);

    public static double FromTrimPrepPhase(double prepFraction) =>
        PostProcessEnd + Math.Clamp(prepFraction, 0, 1) * (TrimPrepEnd - PostProcessEnd);

    public static double TrimPrepCreep(double elapsedFraction)
    {
        var t = Math.Clamp(elapsedFraction, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 2.4);
        return PostProcessEnd + (TrimPrepEnd - PostProcessEnd) * eased;
    }
}
