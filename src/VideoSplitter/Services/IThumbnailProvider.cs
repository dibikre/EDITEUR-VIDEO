using System.Windows.Media;

namespace VideoSplitter.Services;

public interface IThumbnailProvider : IDisposable
{
    ImageSource? GetThumbnail(TimeSpan time);
    bool IsReady { get; }
}
