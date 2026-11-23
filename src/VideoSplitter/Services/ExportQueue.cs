using VideoSplitter.Models;

namespace VideoSplitter.Services;

public interface IExportQueue
{
    Task<IReadOnlyList<ExportResult>> RunAsync(
        IReadOnlyList<ExportJob> jobs,
        int maxConcurrency,
        IProgress<ExportProgress> progress,
        Action<int>? onJobStarted,
        Action<int, ExportResult>? onJobCompleted,
        CancellationToken ct);

    Task<ExportResult> RunSingleVideoAsync(
        SingleVideoExportJob job,
        IProgress<ExportProgress> progress,
        CancellationToken ct);
}

public sealed class ExportQueue : IExportQueue
{
    private readonly IExportService _exportService;

    public ExportQueue(IExportService exportService)
    {
        _exportService = exportService;
    }

    public Task<ExportResult> RunSingleVideoAsync(
        SingleVideoExportJob job,
        IProgress<ExportProgress> progress,
        CancellationToken ct)
    {
        return Task.Run(() => _exportService.ExportSingleVideoAsync(job, progress, ct), CancellationToken.None);
    }

    public async Task<IReadOnlyList<ExportResult>> RunAsync(
        IReadOnlyList<ExportJob> jobs,
        int maxConcurrency,
        IProgress<ExportProgress> progress,
        Action<int>? onJobStarted,
        Action<int, ExportResult>? onJobCompleted,
        CancellationToken ct)
    {
        var results = new ExportResult[jobs.Count];
        var runningTasks = new List<Task>(jobs.Count);
        using var semaphore = new SemaphoreSlim(Math.Clamp(maxConcurrency, 1, 2));

        for (int i = 0; i < jobs.Count; i++)
        {
            ExportJob job = jobs[i];
            int index = i;

            if (job.IsSkipped)
            {
                var skipped = new ExportResult(ExportOutcome.Skipped, null, null, job.OutputPath, null, TimeSpan.Zero);
                results[index] = skipped;
                onJobCompleted?.Invoke(index, skipped);
                continue;
            }

            if (ct.IsCancellationRequested)
            {
                var cancelled = new ExportResult(ExportOutcome.Cancelled, "Export annulé", null, null, null, TimeSpan.Zero);
                results[index] = cancelled;
                onJobCompleted?.Invoke(index, cancelled);
                continue;
            }

            try
            {
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var cancelled = new ExportResult(ExportOutcome.Cancelled, "Export annulé", null, null, null, TimeSpan.Zero);
                results[index] = cancelled;
                onJobCompleted?.Invoke(index, cancelled);
                continue;
            }

            Task jobTask = Task.Run(async () =>
            {
                try
                {
                    onJobStarted?.Invoke(index);
                    ExportResult result = await _exportService.ExportAsync(job, progress, ct).ConfigureAwait(false);
                    results[index] = result;
                    onJobCompleted?.Invoke(index, result);
                }
                catch (Exception ex)
                {
                    var failed = new ExportResult(
                        ExportOutcome.Failed,
                        "Erreur inattendue lors de l'export.",
                        ex.ToString(),
                        null,
                        job.Encoder,
                        TimeSpan.Zero);
                    results[index] = failed;
                    onJobCompleted?.Invoke(index, failed);
                }
                finally
                {
                    semaphore.Release();
                }
            }, CancellationToken.None);

            runningTasks.Add(jobTask);
        }

        await Task.WhenAll(runningTasks).ConfigureAwait(false);
        return results;
    }
}
