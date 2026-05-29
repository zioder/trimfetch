namespace TrimFetch.Models;

/// <summary>Which trim modes are meaningful for the active media.</summary>
public enum TrimMediaProfile
{
    /// <summary>Video file with at least one audio stream.</summary>
    VideoWithAudio,

    /// <summary>Video without an audio track (silent clip).</summary>
    VideoOnly,

    /// <summary>Audio-only file or source (no video stream).</summary>
    AudioOnly,
}
