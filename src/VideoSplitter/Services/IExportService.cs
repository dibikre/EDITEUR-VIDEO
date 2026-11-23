using VideoSplitter.Models;

namespace VideoSplitter.Services;

public interface IExportService
{
    Task<ExportResult> ExportAsync(ExportJob job, IProgress<ExportProgress> progress, CancellationToken ct);
    Task<ExportResult> ExportSingleVideoAsync(SingleVideoExportJob job, IProgress<ExportProgress> progress, CancellationToken ct);
}
