namespace TrimFetch.Models;

public sealed record TrimSelection(double StartSeconds, double EndSeconds)
{
    public double DurationSeconds => Math.Max(0, EndSeconds - StartSeconds);
}
