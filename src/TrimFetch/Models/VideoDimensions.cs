namespace TrimFetch.Models;

public readonly record struct VideoDimensions(int Width, int Height)
{
    public static VideoDimensions Default { get; } = new(1920, 1080);

    public bool IsValid => Width > 0 && Height > 0;

    public double AspectRatio => IsValid ? Width / (double)Height : Default.AspectRatio;

    public VideoDimensions OrDefault() => IsValid ? this : Default;
}
