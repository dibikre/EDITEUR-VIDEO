namespace VideoSplitter.Services;

public interface ICacheService
{
    string GetCacheDirectory(string mediaPath);
    void TouchCacheDirectory(string cacheDirectory);
    Task CleanupAsync(TimeSpan maxAge, CancellationToken ct);
}
