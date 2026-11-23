using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using CliWrap;
using CliWrap.EventStream;
using VideoSplitter.Export;
using VideoSplitter.Models;

namespace VideoSplitter.Services;

public sealed class ExportService : IExportService
{
    private const int MaxStderrLines = 30;
    private const int DeleteRetryCount = 5;
    private const int DeleteRetryDelayMs = 200;
    private const long ProgressThrottleMs = 100;
    private const double ProgressMinDelta = 0.005;

    private readonly IAppLogger _logger;
    private readonly string _ffmpegPath;
    private readonly SingleVideoExportRunner _singleVideoRunner;

    public ExportService(IAppLogger logger)
    {
        _logger = logger;
        string ffmpegDir = Path.Combine(AppContext.BaseDirectory, "Assets", "ffmpeg");
        _ffmpegPath = Path.Combine(ffmpegDir, "ffmpeg.exe");
        string ffprobePath = Path.Combine(ffmpegDir, "ffprobe.exe");
        _singleVideoRunner = new SingleVideoExportRunner(_logger, _ffmpegPath, ffprobePath, ExportAsync);
    }

    public async Task<ExportResult> ExportAsync(
        ExportJob job,
        IProgress<ExportProgress> progress,
        CancellationToken ct)
    {
        if (job.IsSkipped)
        {
            return new ExportResult(ExportOutcome.Skipped, null, null, job.OutputPath, null, TimeSpan.Zero);
        }

        if (ct.IsCancellationRequested)
        {
            return new ExportResult(ExportOutcome.Cancelled, "Export annulé", null, null, null, TimeSpan.Zero);
        }

        if (!File.Exists(_ffmpegPath))
        {
            string detail = $"Chemin attendu : {_ffmpegPath}";
            _logger.Error($"Export impossible : ffmpeg.exe introuvable. {detail}");
            return new ExportResult(
                ExportOutcome.Failed,
                "L'outil ffmpeg.exe est introuvable dans Assets\\ffmpeg.",
                detail,
                null,
                null,
                TimeSpan.Zero);
        }

        ExportResult firstAttempt = await RunAttemptAsync(job, job.Encoder, progress, ct).ConfigureAwait(false);

        if (firstAttempt.Outcome == ExportOutcome.Failed
            && job.Mode == ExportMode.Precise
            && job.AllowCpuFallback
            && job.Encoder != EncoderKind.Cpu
            && !ct.IsCancellationRequested)
        {
            _logger.Warn(
                $"Échec de l'encodeur matériel {job.Encoder} pour '{job.Segment.Name}'. Repli automatique sur CPU (libx264). Détails : {firstAttempt.TechnicalDetails}");

            progress.Report(new ExportProgress(job.Segment.Id, 0.0, TimeSpan.Zero));
            return await RunAttemptAsync(job, EncoderKind.Cpu, progress, ct).ConfigureAwait(false);
        }

        return firstAttempt;
    }

    public Task<ExportResult> ExportSingleVideoAsync(
        SingleVideoExportJob job,
        IProgress<ExportProgress> progress,
        CancellationToken ct)
    {
        return _singleVideoRunner.RunAsync(job, progress, ct);
    }

    private async Task<ExportResult> RunAttemptAsync(
        ExportJob job,
        EncoderKind encoderToUse,
        IProgress<ExportProgress> progress,
        CancellationToken ct)
    {
        string partPath = ExportPlanner.GetTemporaryPartPath(job.OutputPath);
        await DeletePartFileWithRetriesAsync(partPath).ConfigureAwait(false);

        ExportJob effectiveJob = job with { Encoder = encoderToUse };
        List<string> args = FfmpegArgsBuilder.BuildExportArgs(effectiveJob, partPath);
        string commandLineText = FormatCommandLine(args);

        _logger.Info($"Démarrage export [{job.Index}/{job.Total}] Mode={job.Mode}, Encodeur={encoderToUse} : ffmpeg {commandLineText}");

        var stopwatch = Stopwatch.StartNew();
        var stderrLines = new Queue<string>(MaxStderrLines + 1);
        double segmentDurationUs = Math.Max(1.0, job.Segment.Duration.TotalSeconds * 1_000_000.0);
        double lastReportedFraction = -1.0;
        long lastReportTick = Environment.TickCount64;
        int exitCode = -1;

        try
        {
            Command command = Cli.Wrap(_ffmpegPath)
                .WithArguments(args)
                .WithValidation(CommandResultValidation.None);

            await foreach (CommandEvent cmdEvent in command.ListenAsync(
                Encoding.UTF8,
                Encoding.UTF8,
                ct,
                CancellationToken.None).ConfigureAwait(false))
            {
                switch (cmdEvent)
                {
                    case StartedCommandEvent started:
                        if (effectiveJob.Mode == ExportMode.Precise && encoderToUse == EncoderKind.Cpu)
                        {
                            TrySetBelowNormalPriority(started.ProcessId);
                        }
                        break;

                    case StandardOutputCommandEvent stdOut:
                        HandleProgressLine(
                            stdOut.Text,
                            job.Segment.Id,
                            segmentDurationUs,
                            stopwatch,
                            progress,
                            ref lastReportedFraction,
                            ref lastReportTick);
                        break;

                    case StandardErrorCommandEvent stdErr:
                        if (!string.IsNullOrWhiteSpace(stdErr.Text))
                        {
                            if (stderrLines.Count >= MaxStderrLines)
                            {
                                stderrLines.Dequeue();
                            }

                            stderrLines.Enqueue(stdErr.Text);
                        }
                        break;

                    case ExitedCommandEvent exited:
                        exitCode = exited.ExitCode;
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            await DeletePartFileWithRetriesAsync(partPath).ConfigureAwait(false);
            _logger.Info($"Export annulé [{job.Index}/{job.Total}] : {job.OutputPath}");
            return new ExportResult(ExportOutcome.Cancelled, "Export annulé", null, null, encoderToUse, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            await DeletePartFileWithRetriesAsync(partPath).ConfigureAwait(false);
            _logger.Error($"Erreur d'exécution FFmpeg [{job.Index}/{job.Total}] : {job.OutputPath}", ex);
            return new ExportResult(
                ExportOutcome.Failed,
                "Impossible de lancer le processus FFmpeg.",
                ex.ToString(),
                null,
                encoderToUse,
                stopwatch.Elapsed);
        }

        stopwatch.Stop();
        string stderrTail = string.Join(Environment.NewLine, stderrLines);

        if (exitCode == 0 && TryGetNonEmptyFileLength(partPath, out long partSize))
        {
            try
            {
                File.Move(partPath, job.OutputPath, overwrite: true);
                progress.Report(new ExportProgress(job.Segment.Id, 1.0, stopwatch.Elapsed));

                _logger.Info(
                    $"Export réussi [{job.Index}/{job.Total}] : {job.OutputPath} | Encodeur={encoderToUse} | Durée={stopwatch.Elapsed.TotalSeconds:F2} s | Taille={partSize} octets | Code=0");

                return new ExportResult(
                    ExportOutcome.Success,
                    null,
                    null,
                    job.OutputPath,
                    encoderToUse,
                    stopwatch.Elapsed);
            }
            catch (Exception ex)
            {
                await DeletePartFileWithRetriesAsync(partPath).ConfigureAwait(false);
                _logger.Error($"Impossible de finaliser le fichier exporté '{job.OutputPath}'", ex);
                string message = ex is UnauthorizedAccessException
                    ? "Accès refusé au dossier ou au fichier de sortie"
                    : "Impossible d'écrire le fichier de sortie final";
                return new ExportResult(
                    ExportOutcome.Failed,
                    message,
                    ex.ToString(),
                    null,
                    encoderToUse,
                    stopwatch.Elapsed);
            }
        }

        await DeletePartFileWithRetriesAsync(partPath).ConfigureAwait(false);

        string userError = TranslateStderrToFrench(stderrTail, exitCode, effectiveJob.Mode);
        string technicalDetails = $"Commande : ffmpeg {commandLineText}{Environment.NewLine}Code de sortie : {exitCode}{Environment.NewLine}{stderrTail}";
        _logger.Error($"Échec export [{job.Index}/{job.Total}] '{job.OutputPath}' (code {exitCode}) : {stderrTail}");

        return new ExportResult(
            ExportOutcome.Failed,
            userError,
            technicalDetails,
            null,
            encoderToUse,
            stopwatch.Elapsed);
    }

    internal static void HandleProgressLine(
        string line,
        Guid segmentId,
        double segmentDurationUs,
        Stopwatch stopwatch,
        IProgress<ExportProgress> progress,
        ref double lastReportedFraction,
        ref long lastReportTick)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (line.Equals("progress=end", StringComparison.Ordinal))
        {
            if (lastReportedFraction < 1.0)
            {
                lastReportedFraction = 1.0;
                lastReportTick = Environment.TickCount64;
                progress.Report(new ExportProgress(segmentId, 1.0, stopwatch.Elapsed));
            }

            return;
        }

        if (!line.StartsWith("out_time_us=", StringComparison.Ordinal))
        {
            return;
        }

        string rawValue = line.Substring("out_time_us=".Length).Trim();
        if (rawValue.Equals("N/A", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long us) || us < 0)
        {
            return;
        }

        double fraction = Math.Clamp(us / segmentDurationUs, 0.0, 1.0);
        long now = Environment.TickCount64;
        if (lastReportedFraction < 0.0
            || fraction - lastReportedFraction >= ProgressMinDelta
            || now - lastReportTick >= ProgressThrottleMs)
        {
            lastReportedFraction = fraction;
            lastReportTick = now;
            progress.Report(new ExportProgress(segmentId, fraction, stopwatch.Elapsed));
        }
    }

    internal static void TrySetBelowNormalPriority(int processId)
    {
        try
        {
            using var proc = Process.GetProcessById(processId);
            proc.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
            // Ignorer si le processus est deja termine ou inaccessible.
        }
    }

    internal static bool TryGetNonEmptyFileLength(string path, out long length)
    {
        length = 0;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length <= 0)
            {
                return false;
            }

            length = fi.Length;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static async Task DeleteFileWithRetriesAsync(string partPath, IAppLogger logger)
    {
        for (int attempt = 0; attempt < DeleteRetryCount; attempt++)
        {
            try
            {
                if (!File.Exists(partPath))
                {
                    return;
                }

                File.Delete(partPath);
                return;
            }
            catch (Exception ex) when (attempt < DeleteRetryCount - 1)
            {
                logger.Warn($"Nouvelle tentative de suppression de '{partPath}' ({attempt + 1}/{DeleteRetryCount}) : {ex.Message}");
                await Task.Delay(DeleteRetryDelayMs, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Warn($"Impossible de supprimer le fichier partiel '{partPath}' : {ex.Message}");
                return;
            }
        }
    }

    private Task DeletePartFileWithRetriesAsync(string partPath)
        => DeleteFileWithRetriesAsync(partPath, _logger);

    internal static string TranslateStderrToFrench(string stderr, int exitCode, ExportMode mode)
    {
        if (stderr.Contains("No space left on device", StringComparison.OrdinalIgnoreCase))
        {
            return "Disque plein";
        }

        if (stderr.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
        {
            return "Accès refusé au dossier ou au fichier de sortie";
        }

        if (stderr.Contains("Invalid data found", StringComparison.OrdinalIgnoreCase))
        {
            return "Données invalides dans le fichier source";
        }

        if (mode == ExportMode.Fast
            && (stderr.Contains("codec not currently supported in container", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("Could not write header", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("Error initializing output stream", StringComparison.OrdinalIgnoreCase)))
        {
            return "Ce format ne permet pas la copie directe de ce segment. Utilisez le mode Précis.";
        }

        return $"Échec de l'export (code {exitCode})";
    }

    private static string FormatCommandLine(IReadOnlyList<string> args)
    {
        return string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
    }
}
