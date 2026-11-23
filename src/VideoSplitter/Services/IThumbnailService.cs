using VideoSplitter.Models;

namespace VideoSplitter.Services;

public interface IThumbnailService
{
    event EventHandler<int>? SheetReady;
    event EventHandler? SheetsReset;

    SpriteSet ComputeLayout(MediaInfo media);
    Task<SpriteSet> GetOrCreateAsync(MediaInfo media, IProgress<double>? progress, CancellationToken ct);
}
