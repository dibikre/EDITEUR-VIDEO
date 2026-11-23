using System.Windows.Media;
using VideoSplitter.Models;

namespace VideoSplitter.Services;

public interface IThumbnailProvider : IDisposable
{
    SpriteSet Layout { get; }
    bool IsReady { get; }

    event EventHandler? ThumbnailsChanged;

    ImageSource? GetThumbnail(TimeSpan time);
    int IndexForTime(TimeSpan time);
    Brush? GetThumbnailBrush(int index);
}
