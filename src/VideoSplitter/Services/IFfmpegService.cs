using VideoSplitter.Models;

namespace VideoSplitter.Services;

public interface IFfmpegService
{
    Task<MediaInfo> ProbeAsync(string path, CancellationToken ct);
    Task<IReadOnlyList<double>> GetKeyframesAsync(string path, MediaInfo media, CancellationToken ct);
    Task<EncoderCapabilities> DetectEncodersAsync(CancellationToken ct);
}
