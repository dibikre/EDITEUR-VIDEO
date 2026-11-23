using Avalonia;
using Avalonia.Media;
using VideoSplitter.Models;

namespace VideoSplitter.Services;

public interface IThumbnailProvider : IDisposable
{
    SpriteSet Layout { get; }
    bool IsReady { get; }

    event EventHandler? ThumbnailsChanged;

    IImage? GetThumbnail(TimeSpan time);
    int IndexForTime(TimeSpan time);
    bool TryGetThumbnailTile(int index, out IImage? sheet, out Rect sourceRect);
}
