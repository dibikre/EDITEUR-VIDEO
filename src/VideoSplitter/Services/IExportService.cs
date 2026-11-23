using VideoSplitter.Models;

namespace VideoSplitter.Services;

public interface IExportService
{
    Task<ExportResult> ExportAsync(ExportJob job, IProgress<ExportProgress> progress, CancellationToken ct);
}
