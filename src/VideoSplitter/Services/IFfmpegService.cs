using VideoSplitter.Models;

namespace VideoSplitter.Services;

public interface IFfmpegService
{
    Task<MediaInfo> ProbeAsync(string path, CancellationToken ct);
}
