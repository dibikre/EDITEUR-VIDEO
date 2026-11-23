namespace VideoSplitter.Models;

public sealed record Segment(
    Guid Id,
    TimeSpan Start,
    TimeSpan End,
    bool Enabled,
    string Name)
{
    public TimeSpan Duration => End - Start;
}
