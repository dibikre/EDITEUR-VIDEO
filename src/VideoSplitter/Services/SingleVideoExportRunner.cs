using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using CliWrap.EventStream;
using VideoSplitter.Export;
using VideoSplitter.Models;

namespace VideoSplitter.Services;

internal sealed class SingleVideoExportRunner
{
    private const int MaxStderrLines = 30;
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly IAppLogger _logger;
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly Func<ExportJob, IProgress<ExportProgress>, CancellationToken, Task<ExportResult>> _singleSegmentExporter;

    public SingleVideoExportRunner(
        IAppLogger logger,
        string ffmpegPath,
        string ffprobePath,
        Func<ExportJob, IProgress<ExportProgress>, CancellationToken, Task<ExportResult>> singleSegmentExporter)
    {
        _logger = logger;
        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath;
        _singleSegmentExporter = singleSegmentExporter;
    }

    public async Task<ExportResult> RunAsync(
        SingleVideoExportJob job,
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

        if (job.Segments.Count == 0)
        {
            return new ExportResult(ExportOutcome.Failed, "Aucun segment à exporter.", null, null, null, TimeSpan.Zero);
        }

        if (!File.Exists(_ffmpegPath))
        {
            string detail = $"Chemin attendu : {_ffmpegPath}";
            _logger.Error($"Export vidéo unique impossible : ffmpeg.exe introuvable. {detail}");
            return new ExportResult(
                ExportOutcome.Failed,
                "L'outil ffmpeg.exe est introuvable dans Assets\\ffmpeg.",
                detail,
                null,
                null,
                TimeSpan.Zero);
        }

        if (job.Mode == ExportMode.Precise)
        {
            if (job.Segments.Count == 1)
            {
                return await RunAsSingleSegmentAsync(job, progress, ct).ConfigureAwait(false);
            }

            return await RunPreciseMergedAsync(job, progress, ct).ConfigureAwait(false);
        }

        if (job.Segments.Count == 1 && (!job.Media.HasAudio || job.Segments[0].Start <= TimeSpan.FromMilliseconds(1)))
        {
            return await RunAsSingleSegmentAsync(job, progress, ct).ConfigureAwait(false);
        }

        if (!File.Exists(_ffprobePath))
        {
            string detail = $"Chemin attendu : {_ffprobePath}";
            _logger.Error($"Export vidéo unique rapide impossible : ffprobe.exe introuvable. {detail}");
            return new ExportResult(
                ExportOutcome.Failed,
                "L'outil ffprobe.exe est introuvable dans Assets\\ffmpeg.",
                detail,
                null,
                null,
                TimeSpan.Zero);
        }

        return await RunFastMergedAsync(job, progress, ct).ConfigureAwait(false);
    }

    private Task<ExportResult> RunAsSingleSegmentAsync(
        SingleVideoExportJob job,
        IProgress<ExportProgress> progress,
        CancellationToken ct)
    {
        Segment seg = job.Segments[0];
        var singleJob = new ExportJob(
            Media: job.Media,
            Segment: seg,
            Index: 1,
            Total: 1,
            OutputPath: job.OutputPath,
            Mode: job.Mode,
            Encoder: job.Encoder,
            Crf: job.Crf,
            AllowCpuFallback: job.AllowCpuFallback,
            IsSkipped: job.IsSkipped);

        var mappedProgress = new Progress<ExportProgress>(p =>
            progress.Report(new ExportProgress(job.JobId, p.Fraction, p.Elapsed)));

        return _singleSegmentExporter(singleJob, mappedProgress, ct);
    }

    private async Task<ExportResult> RunFastMergedAsync(
        SingleVideoExportJob job,
        IProgress<ExportProgress> progress,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        string partPath = ExportPlanner.GetTemporaryPartPath(job.OutputPath);
        string tempDir = Path.Combine(Path.GetTempPath(), "VideoSplitter", $"merge_{Guid.NewGuid():N}");
        await ExportService.DeleteFileWithRetriesAsync(partPath, _logger).ConfigureAwait(false);

        try
        {
            Directory.CreateDirectory(tempDir);

            string interExt = ResolveIntermediateExtension(Path.GetExtension(job.OutputPath));
            double totalWeightSec = 0.0;
            for (int i = 0; i < job.Segments.Count; i++)
            {
                totalWeightSec += Math.Max(0.1, job.Segments[i].Duration.TotalSeconds);
            }
            totalWeightSec = Math.Max(0.1, totalWeightSec);

            var videoChunks = new List<(string Path, double DurationSec)>(job.Segments.Count);
            var audioChunks = new List<(string Path, double DurationSec)>(job.Segments.Count);
            bool includeAudio = job.Media.HasAudio;
            double completedSec = 0.0;
            double totalVideoDurationSec = 0.0;

            for (int i = 0; i < job.Segments.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                Segment seg = job.Segments[i];

                string vChunkPath = Path.Combine(tempDir, $"v_{i:D4}{interExt}");
                List<string> vArgs = FfmpegArgsBuilder.BuildFastVideoChunkArgs(job.Media.Path, seg.Start, seg.Duration, vChunkPath);
                (int vCode, string vStderr) = await RunSimpleFfmpegAsync(vArgs, ct).ConfigureAwait(false);
                if (vCode != 0 || !ExportService.TryGetNonEmptyFileLength(vChunkPath, out _))
                {
                    string cmd = FormatCommandLine(vArgs);
                    string userErr = ExportService.TranslateStderrToFrench(vStderr, vCode, ExportMode.Fast);
                    return new ExportResult(
                        ExportOutcome.Failed,
                        userErr,
                        $"Commande : ffmpeg {cmd}{Environment.NewLine}Code de sortie : {vCode}{Environment.NewLine}{vStderr}",
                        null,
                        null,
                        stopwatch.Elapsed);
                }

                double chunkVideoDur = await ProbeVideoDurationAsync(vChunkPath, job.Media.Fps, ct).ConfigureAwait(false);
                if (chunkVideoDur <= 0.0)
                {
                    chunkVideoDur = Math.Max(0.1, seg.Duration.TotalSeconds);
                }

                videoChunks.Add((vChunkPath, chunkVideoDur));
                totalVideoDurationSec += chunkVideoDur;

                if (includeAudio)
                {
                    double videoStartSec = ResolveSegmentVideoStartSeconds(seg.Start, job.Keyframes, job.Media.Fps);
                    double audioTargetDur = Math.Max(0.04, chunkVideoDur - 0.020);
                    string aChunkPath = Path.Combine(tempDir, $"a_{i:D4}{interExt}");

                    List<string> aArgs = FfmpegArgsBuilder.BuildFastAudioChunkArgs(
                        job.Media.Path,
                        videoStartSec,
                        audioTargetDur,
                        aChunkPath);

                    (int aCode, string aStderr) = await RunSimpleFfmpegAsync(aArgs, ct).ConfigureAwait(false);
                    if (aCode != 0 || !ExportService.TryGetNonEmptyFileLength(aChunkPath, out _))
                    {
                        _logger.Warn($"Piste audio indisponible pour le segment #{i + 1} lors de la fusion rapide : {aStderr}");
                        includeAudio = false;
                    }
                    else
                    {
                        double actualAudioDur = await ProbeAudioDurationAsync(aChunkPath, ct).ConfigureAwait(false);
                        if (actualAudioDur > chunkVideoDur + 0.002 && chunkVideoDur > 0.08)
                        {
                            double tighterDur = Math.Max(0.04, chunkVideoDur - 0.045);
                            List<string> retryArgs = FfmpegArgsBuilder.BuildFastAudioChunkArgs(
                                job.Media.Path,
                                videoStartSec,
                                tighterDur,
                                aChunkPath);
                            (int retryCode, _) = await RunSimpleFfmpegAsync(retryArgs, ct).ConfigureAwait(false);
                            if (retryCode != 0 || !ExportService.TryGetNonEmptyFileLength(aChunkPath, out _))
                            {
                                includeAudio = false;
                            }
                        }

                        if (includeAudio)
                        {
                            audioChunks.Add((aChunkPath, chunkVideoDur));
                        }
                    }
                }

                completedSec += Math.Max(0.1, seg.Duration.TotalSeconds);
                double prepFraction = Math.Clamp((completedSec / totalWeightSec) * 0.75, 0.0, 0.75);
                progress.Report(new ExportProgress(job.JobId, prepFraction, stopwatch.Elapsed));
            }

            ct.ThrowIfCancellationRequested();

            string videoListPath = Path.Combine(tempDir, "video_concat.txt");
            await WriteFfconcatFileAsync(videoListPath, videoChunks, ct).ConfigureAwait(false);

            string? audioListPath = null;
            if (includeAudio && audioChunks.Count == videoChunks.Count)
            {
                audioListPath = Path.Combine(tempDir, "audio_concat.txt");
                await WriteFfconcatFileAsync(audioListPath, audioChunks, ct).ConfigureAwait(false);
            }

            List<string> concatArgs = FfmpegArgsBuilder.BuildFastConcatArgs(videoListPath, audioListPath, partPath);
            _logger.Info($"Démarrage fusion rapide ({job.Segments.Count} segments) : ffmpeg {FormatCommandLine(concatArgs)}");

            double concatTotalUs = Math.Max(1.0, totalVideoDurationSec * 1_000_000.0);
            var concatProgress = new Progress<ExportProgress>(p =>
            {
                double combined = Math.Clamp(0.75 + (p.Fraction * 0.25), 0.75, 1.0);
                progress.Report(new ExportProgress(job.JobId, combined, stopwatch.Elapsed));
            });

            (int exitCode, string stderrTail) = await RunFfmpegWithProgressAsync(
                concatArgs,
                job.JobId,
                concatTotalUs,
                belowNormalPriority: false,
                stopwatch,
                concatProgress,
                ct).ConfigureAwait(false);

            if (exitCode == 0 && ExportService.TryGetNonEmptyFileLength(partPath, out long finalBytes))
            {
                File.Move(partPath, job.OutputPath, overwrite: true);
                progress.Report(new ExportProgress(job.JobId, 1.0, stopwatch.Elapsed));
                _logger.Info($"Fusion rapide réussie : {job.OutputPath} | Segments={job.Segments.Count} | Durée={stopwatch.Elapsed.TotalSeconds:F2} s | Taille={finalBytes} octets");

                return new ExportResult(
                    ExportOutcome.Success,
                    null,
                    null,
                    job.OutputPath,
                    null,
                    stopwatch.Elapsed);
            }

            await ExportService.DeleteFileWithRetriesAsync(partPath, _logger).ConfigureAwait(false);
            string userError = ExportService.TranslateStderrToFrench(stderrTail, exitCode, ExportMode.Fast);
            string techDetails = $"Commande : ffmpeg {FormatCommandLine(concatArgs)}{Environment.NewLine}Code de sortie : {exitCode}{Environment.NewLine}{stderrTail}";
            _logger.Error($"Échec fusion rapide '{job.OutputPath}' (code {exitCode}) : {stderrTail}");

            return new ExportResult(ExportOutcome.Failed, userError, techDetails, null, null, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            await ExportService.DeleteFileWithRetriesAsync(partPath, _logger).ConfigureAwait(false);
            _logger.Info($"Fusion rapide annulée : {job.OutputPath}");
            return new ExportResult(ExportOutcome.Cancelled, "Export annulé", null, null, null, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            await ExportService.DeleteFileWithRetriesAsync(partPath, _logger).ConfigureAwait(false);
            _logger.Error($"Erreur inattendue lors de la fusion rapide '{job.OutputPath}'", ex);
            return new ExportResult(ExportOutcome.Failed, "Erreur lors de l'assemblage de la vidéo.", ex.ToString(), null, null, stopwatch.Elapsed);
        }
        finally
        {
            TryDeleteDirectoryRecursive(tempDir);
        }
    }

    private async Task<ExportResult> RunPreciseMergedAsync(
        SingleVideoExportJob job,
        IProgress<ExportProgress> progress,
        CancellationToken ct)
    {
        ExportResult firstAttempt = await RunPreciseAttemptAsync(job, job.Encoder, progress, ct).ConfigureAwait(false);

        if (firstAttempt.Outcome == ExportOutcome.Failed
            && job.AllowCpuFallback
            && job.Encoder != EncoderKind.Cpu
            && !ct.IsCancellationRequested)
        {
            _logger.Warn(
                $"Échec de l'encodeur matériel {job.Encoder} pour la vidéo fusionnée. Repli automatique sur CPU (libx264). Détails : {firstAttempt.TechnicalDetails}");

            progress.Report(new ExportProgress(job.JobId, 0.0, TimeSpan.Zero));
            return await RunPreciseAttemptAsync(job, EncoderKind.Cpu, progress, ct).ConfigureAwait(false);
        }

        return firstAttempt;
    }

    private async Task<ExportResult> RunPreciseAttemptAsync(
        SingleVideoExportJob job,
        EncoderKind encoderToUse,
        IProgress<ExportProgress> progress,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        string partPath = ExportPlanner.GetTemporaryPartPath(job.OutputPath);
        string tempDir = Path.Combine(Path.GetTempPath(), "VideoSplitter", $"merge_precise_{Guid.NewGuid():N}");
        await ExportService.DeleteFileWithRetriesAsync(partPath, _logger).ConfigureAwait(false);

        try
        {
            Directory.CreateDirectory(tempDir);

            string filterScriptPath = Path.Combine(tempDir, "filter_concat.txt");
            string filterScript = FfmpegArgsBuilder.BuildPreciseConcatFilterScript(job.Segments.Count, job.Media.HasAudio);
            await File.WriteAllTextAsync(filterScriptPath, filterScript, Utf8NoBom, ct).ConfigureAwait(false);

            SingleVideoExportJob effectiveJob = job with { Encoder = encoderToUse };
            List<string> args = FfmpegArgsBuilder.BuildPreciseConcatArgs(effectiveJob, filterScriptPath, partPath);
            _logger.Info($"Démarrage fusion précise ({job.Segments.Count} segments, Encodeur={encoderToUse}) : ffmpeg {FormatCommandLine(args)}");

            double totalUs = 0.0;
            for (int i = 0; i < job.Segments.Count; i++)
            {
                totalUs += Math.Max(0.1, job.Segments[i].Duration.TotalSeconds) * 1_000_000.0;
            }
            totalUs = Math.Max(1.0, totalUs);

            (int exitCode, string stderrTail) = await RunFfmpegWithProgressAsync(
                args,
                job.JobId,
                totalUs,
                belowNormalPriority: encoderToUse == EncoderKind.Cpu,
                stopwatch,
                progress,
                ct).ConfigureAwait(false);

            if (exitCode == 0 && ExportService.TryGetNonEmptyFileLength(partPath, out long partSize))
            {
                File.Move(partPath, job.OutputPath, overwrite: true);
                progress.Report(new ExportProgress(job.JobId, 1.0, stopwatch.Elapsed));
                _logger.Info($"Fusion précise réussie : {job.OutputPath} | Encodeur={encoderToUse} | Durée={stopwatch.Elapsed.TotalSeconds:F2} s | Taille={partSize} octets");

                return new ExportResult(ExportOutcome.Success, null, null, job.OutputPath, encoderToUse, stopwatch.Elapsed);
            }

            await ExportService.DeleteFileWithRetriesAsync(partPath, _logger).ConfigureAwait(false);
            string userError = ExportService.TranslateStderrToFrench(stderrTail, exitCode, ExportMode.Precise);
            string techDetails = $"Commande : ffmpeg {FormatCommandLine(args)}{Environment.NewLine}Code de sortie : {exitCode}{Environment.NewLine}{stderrTail}";
            _logger.Error($"Échec fusion précise '{job.OutputPath}' (code {exitCode}) : {stderrTail}");

            return new ExportResult(ExportOutcome.Failed, userError, techDetails, null, encoderToUse, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            await ExportService.DeleteFileWithRetriesAsync(partPath, _logger).ConfigureAwait(false);
            _logger.Info($"Fusion précise annulée : {job.OutputPath}");
            return new ExportResult(ExportOutcome.Cancelled, "Export annulé", null, null, encoderToUse, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            await ExportService.DeleteFileWithRetriesAsync(partPath, _logger).ConfigureAwait(false);
            _logger.Error($"Erreur d'exécution FFmpeg lors de la fusion précise '{job.OutputPath}'", ex);
            return new ExportResult(ExportOutcome.Failed, "Impossible de lancer le processus FFmpeg.", ex.ToString(), null, encoderToUse, stopwatch.Elapsed);
        }
        finally
        {
            TryDeleteDirectoryRecursive(tempDir);
        }
    }

    private async Task<(int ExitCode, string StderrTail)> RunSimpleFfmpegAsync(List<string> args, CancellationToken ct)
    {
        var stderrLines = new Queue<string>(MaxStderrLines + 1);
        object stderrLock = new();

        void OnStderr(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (stderrLock)
            {
                if (stderrLines.Count >= MaxStderrLines)
                {
                    stderrLines.Dequeue();
                }

                stderrLines.Enqueue(line);
            }
        }

        CommandResult result = await Cli.Wrap(_ffmpegPath)
            .WithArguments(args)
            .WithValidation(CommandResultValidation.None)
            .WithStandardErrorPipe(PipeTarget.ToDelegate(OnStderr))
            .ExecuteAsync(ct, CancellationToken.None)
            .ConfigureAwait(false);

        string tail;
        lock (stderrLock)
        {
            tail = string.Join(Environment.NewLine, stderrLines);
        }

        return (result.ExitCode, tail);
    }

    private async Task<(int ExitCode, string StderrTail)> RunFfmpegWithProgressAsync(
        List<string> args,
        Guid progressId,
        double totalDurationUs,
        bool belowNormalPriority,
        Stopwatch stopwatch,
        IProgress<ExportProgress> progress,
        CancellationToken ct)
    {
        var stderrLines = new Queue<string>(MaxStderrLines + 1);
        double lastReportedFraction = -1.0;
        long lastReportTick = Environment.TickCount64;
        int exitCode = -1;

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
                    if (belowNormalPriority)
                    {
                        ExportService.TrySetBelowNormalPriority(started.ProcessId);
                    }
                    break;

                case StandardOutputCommandEvent stdOut:
                    ExportService.HandleProgressLine(
                        stdOut.Text,
                        progressId,
                        totalDurationUs,
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

        return (exitCode, string.Join(Environment.NewLine, stderrLines));
    }

    private async Task<double> ProbeVideoDurationAsync(string chunkPath, double fallbackFps, CancellationToken ct)
    {
        try
        {
            BufferedCommandResult res = await Cli.Wrap(_ffprobePath)
                .WithArguments([
                    "-v", "error",
                    "-count_packets",
                    "-select_streams", "v:0",
                    "-show_entries", "format=duration:stream=duration,nb_read_packets,avg_frame_rate,r_frame_rate",
                    "-of", "json",
                    chunkPath
                ])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(Encoding.UTF8, Encoding.UTF8, ct, CancellationToken.None)
                .ConfigureAwait(false);

            if (res.ExitCode != 0 || string.IsNullOrWhiteSpace(res.StandardOutput))
            {
                return 0.0;
            }

            using JsonDocument doc = JsonDocument.Parse(res.StandardOutput);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("streams", out JsonElement streams)
                && streams.ValueKind == JsonValueKind.Array
                && streams.GetArrayLength() > 0)
            {
                JsonElement stream = streams[0];
                double streamDur = ParsePositiveDoubleProperty(stream, "duration");
                if (streamDur > 0.0)
                {
                    return streamDur;
                }

                double packetCount = ParsePositiveDoubleProperty(stream, "nb_read_packets");
                if (packetCount > 0.0)
                {
                    double fps = ParseFractionString(GetStringProperty(stream, "avg_frame_rate"));
                    if (fps <= 0.0)
                    {
                        fps = ParseFractionString(GetStringProperty(stream, "r_frame_rate"));
                    }
                    if (fps <= 0.0)
                    {
                        fps = fallbackFps > 0.0 ? fallbackFps : 30.0;
                    }

                    return packetCount / fps;
                }
            }

            if (root.TryGetProperty("format", out JsonElement format))
            {
                double formatDur = ParsePositiveDoubleProperty(format, "duration");
                if (formatDur > 0.0)
                {
                    return formatDur;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Impossible de mesurer la durée exacte du segment vidéo '{chunkPath}' : {ex.Message}");
        }

        return 0.0;
    }

    private async Task<double> ProbeAudioDurationAsync(string chunkPath, CancellationToken ct)
    {
        try
        {
            BufferedCommandResult res = await Cli.Wrap(_ffprobePath)
                .WithArguments([
                    "-v", "error",
                    "-select_streams", "a:0",
                    "-show_entries", "format=duration:stream=duration",
                    "-of", "json",
                    chunkPath
                ])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(Encoding.UTF8, Encoding.UTF8, ct, CancellationToken.None)
                .ConfigureAwait(false);

            if (res.ExitCode != 0 || string.IsNullOrWhiteSpace(res.StandardOutput))
            {
                return 0.0;
            }

            using JsonDocument doc = JsonDocument.Parse(res.StandardOutput);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("streams", out JsonElement streams)
                && streams.ValueKind == JsonValueKind.Array
                && streams.GetArrayLength() > 0)
            {
                double streamDur = ParsePositiveDoubleProperty(streams[0], "duration");
                if (streamDur > 0.0)
                {
                    return streamDur;
                }
            }

            if (root.TryGetProperty("format", out JsonElement format))
            {
                return ParsePositiveDoubleProperty(format, "duration");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Ignorer si la sonde audio échoue.
        }

        return 0.0;
    }

    internal static double ResolveSegmentVideoStartSeconds(
        TimeSpan requestedStart,
        IReadOnlyList<double> keyframes,
        double fps)
    {
        double req = Math.Max(0.0, requestedStart.TotalSeconds);
        if (req <= 0.001 || keyframes.Count == 0)
        {
            return req;
        }

        double safeFps = fps > 0.0 ? fps : 30.0;
        double epsilon = Math.Min(0.005, 0.25 / safeFps);
        double limit = req + epsilon;

        int lo = 0;
        int hi = keyframes.Count - 1;
        int bestIndex = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (keyframes[mid] <= limit)
            {
                bestIndex = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return bestIndex >= 0 ? Math.Max(0.0, keyframes[bestIndex]) : req;
    }

    private static async Task WriteFfconcatFileAsync(
        string listPath,
        IReadOnlyList<(string Path, double DurationSec)> entries,
        CancellationToken ct)
    {
        var sb = new StringBuilder(entries.Count * 96 + 24);
        sb.AppendLine("ffconcat version 1.0");
        for (int i = 0; i < entries.Count; i++)
        {
            string escapedPath = EscapeFfconcatPath(entries[i].Path);
            string dur = Math.Max(0.01, entries[i].DurationSec).ToString("0.######", CultureInfo.InvariantCulture);
            sb.Append("file '").Append(escapedPath).AppendLine("'");
            sb.Append("duration ").AppendLine(dur);
        }

        await File.WriteAllTextAsync(listPath, sb.ToString(), Utf8NoBom, ct).ConfigureAwait(false);
    }

    private static string EscapeFfconcatPath(string filePath)
    {
        return filePath
            .Replace('\\', '/')
            .Replace("'", "'\\''", StringComparison.Ordinal);
    }

    private static string ResolveIntermediateExtension(string? outputExtension)
    {
        if (string.Equals(outputExtension, ".mp4", StringComparison.OrdinalIgnoreCase)
            || string.Equals(outputExtension, ".m4v", StringComparison.OrdinalIgnoreCase))
        {
            return ".mp4";
        }

        if (string.Equals(outputExtension, ".mov", StringComparison.OrdinalIgnoreCase))
        {
            return ".mov";
        }

        if (string.Equals(outputExtension, ".webm", StringComparison.OrdinalIgnoreCase))
        {
            return ".webm";
        }

        return ".mkv";
    }

    private static string? GetStringProperty(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static double ParsePositiveDoubleProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement prop))
        {
            return 0.0;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out double numVal))
        {
            return double.IsFinite(numVal) && numVal > 0.0 ? numVal : 0.0;
        }

        if (prop.ValueKind == JsonValueKind.String
            && double.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double strVal)
            && double.IsFinite(strVal)
            && strVal > 0.0)
        {
            return strVal;
        }

        return 0.0;
    }

    private static double ParseFractionString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0.0;
        }

        int slash = raw.IndexOf('/');
        if (slash > 0)
        {
            if (double.TryParse(raw.AsSpan(0, slash), NumberStyles.Float, CultureInfo.InvariantCulture, out double num)
                && double.TryParse(raw.AsSpan(slash + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double den)
                && den > 0.0)
            {
                return num / den;
            }

            return 0.0;
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double val) && val > 0.0
            ? val
            : 0.0;
    }

    private static string FormatCommandLine(IReadOnlyList<string> args)
    {
        return string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
    }

    private static void TryDeleteDirectoryRecursive(string directoryPath)
    {
        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
        catch
        {
            // Ignorer le nettoyage si un fichier temporaire est encore verrouillé.
        }
    }
}
