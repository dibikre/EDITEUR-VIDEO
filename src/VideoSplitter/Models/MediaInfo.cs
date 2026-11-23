namespace VideoSplitter.Models;

public sealed record MediaInfo(
    string Path,
    TimeSpan Duration,
    int Width,
    int Height,
    double Fps,
    string VideoCodec,
    string? AudioCodec,
    bool HasAudio,
    TimeSpan StartTime = default);
