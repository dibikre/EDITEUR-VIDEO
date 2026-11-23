using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using CliWrap;
using VideoSplitter.Models;

namespace VideoSplitter.Services;

public sealed class ThumbnailService : IThumbnailService
{
    private const int ThumbWidth = 160;
    private const int GridColumns = 10;
    private const int GridRows = 10;
    private const int CellsPerSheet = GridColumns * GridRows;
    private const int MaxStderrLines = 20;
    private const int CurrentMetaVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly record struct FfmpegPassStats(
        int ExitCode,
        int SheetsProduced,
        int DecodedFrames,
        int NonBlackFrames,
        int TrailingBlackFrames,
        string StderrTail);

    private readonly ICacheService _cacheService;
    private readonly IAppLogger _logger;
    private readonly string _ffmpegPath;

    public event EventHandler<int>? SheetReady;
    public event EventHandler? SheetsReset;

    public ThumbnailService(ICacheService cacheService, IAppLogger logger)
    {
        _cacheService = cacheService;
        _logger = logger;
        _ffmpegPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ffmpeg", "ffmpeg.exe");
    }

    public SpriteSet ComputeLayout(MediaInfo media)
    {
        double durationSeconds = Math.Max(0.1, media.Duration.TotalSeconds);
        double intervalSeconds = Math.Max(1.0, Math.Ceiling(durationSeconds / 600.0));

        int rawHeight = media.Width > 0 && media.Height > 0
            ? (int)Math.Round(ThumbWidth * (double)media.Height / media.Width)
            : 90;
        int thumbHeight = Math.Max(2, rawHeight % 2 == 0 ? rawHeight : rawHeight + 1);

        int count = Math.Max(1, (int)Math.Round(durationSeconds / intervalSeconds, MidpointRounding.AwayFromZero));
        int sheetCount = Math.Max(1, (int)Math.Ceiling(count / (double)CellsPerSheet));
        string directory = _cacheService.GetCacheDirectory(media.Path);

        return new SpriteSet(
            Directory: directory,
            IntervalSeconds: intervalSeconds,
            ThumbWidth: ThumbWidth,
            ThumbHeight: thumbHeight,
            Columns: GridColumns,
            Rows: GridRows,
            Count: count,
            SheetCount: sheetCount,
            ValidFrameCount: count);
    }

    public async Task<SpriteSet> GetOrCreateAsync(MediaInfo media, IProgress<double>? progress, CancellationToken ct)
    {
        SpriteSet layout = ComputeLayout(media);

        if (TryLoadValidCache(layout, out SpriteSet? cachedLayout) && cachedLayout is not null)
        {
            _logger.Info($"Cache hit pour les vignettes ({cachedLayout.SheetCount} feuille(s)) : {cachedLayout.Directory}");
            progress?.Report(1.0);
            for (int i = 0; i < cachedLayout.SheetCount; i++)
            {
                SheetReady?.Invoke(this, i);
            }

            return cachedLayout;
        }

        _logger.Info($"Cache miss pour les vignettes : {layout.Directory}");

        if (!File.Exists(_ffmpegPath))
        {
            throw new FileNotFoundException($"ffmpeg.exe est introuvable : {_ffmpegPath}", _ffmpegPath);
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            FfmpegPassStats stats = await GenerateSpritesWithFallbackAsync(media, layout, progress, ct).ConfigureAwait(false);
            int validFrames = stats.DecodedFrames > 0 ? stats.DecodedFrames : layout.Count;
            SpriteSet finalLayout = layout with
            {
                SheetCount = stats.SheetsProduced,
                ValidFrameCount = validFrames
            };

            await WriteMetadataAsync(media, finalLayout, ct).ConfigureAwait(false);
            progress?.Report(1.0);

            stopwatch.Stop();
            _logger.Info(
                $"Vignettes generees en {stopwatch.Elapsed.TotalSeconds:F2} s ({finalLayout.SheetCount} feuille(s), {finalLayout.ValidFrameCount}/{finalLayout.Count} image(s), intervalle {finalLayout.IntervalSeconds:0} s) : {finalLayout.Directory}");

            return finalLayout;
        }
        catch (OperationCanceledException)
        {
            _logger.Info($"Generation des vignettes annulee : {layout.Directory}");
            CleanupSpriteFiles(layout.Directory);
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"Echec de generation des vignettes ({media.Path})", ex);
            CleanupSpriteFiles(layout.Directory);
            throw;
        }
    }

    private bool TryLoadValidCache(SpriteSet expectedLayout, out SpriteSet? cachedLayout)
    {
        cachedLayout = null;
        string metaPath = Path.Combine(expectedLayout.Directory, "meta.json");
        if (!File.Exists(metaPath))
        {
            return false;
        }

        try
        {
            string json = File.ReadAllText(metaPath);
            SpriteMetaJson? meta = JsonSerializer.Deserialize<SpriteMetaJson>(json, JsonOptions);
            if (meta is null
                || meta.Version < CurrentMetaVersion
                || !meta.Complete
                || meta.Count <= 0
                || meta.Cols <= 0
                || meta.Rows <= 0
                || meta.ThumbWidth != expectedLayout.ThumbWidth
                || meta.ThumbHeight != expectedLayout.ThumbHeight)
            {
                return false;
            }

            int validFrames = meta.ValidFrameCount > 0 ? meta.ValidFrameCount : meta.Count;
            int requiredFrameCells = Math.Min(meta.Count, validFrames);
            int expectedSheets = meta.SheetCount > 0
                ? meta.SheetCount
                : Math.Max(1, (int)Math.Ceiling(requiredFrameCells / (double)(meta.Cols * meta.Rows)));

            for (int i = 0; i < expectedSheets; i++)
            {
                string sheetPath = GetSheetPath(expectedLayout.Directory, i);
                var fileInfo = new FileInfo(sheetPath);
                if (!fileInfo.Exists || fileInfo.Length == 0)
                {
                    return false;
                }
            }

            cachedLayout = new SpriteSet(
                Directory: expectedLayout.Directory,
                IntervalSeconds: meta.Interval > 0 ? meta.Interval : expectedLayout.IntervalSeconds,
                ThumbWidth: meta.ThumbWidth,
                ThumbHeight: meta.ThumbHeight,
                Columns: meta.Cols,
                Rows: meta.Rows,
                Count: meta.Count,
                SheetCount: expectedSheets,
                ValidFrameCount: validFrames);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"meta.json invalide dans {expectedLayout.Directory} : {ex.Message}");
            return false;
        }
    }

    private async Task<FfmpegPassStats> GenerateSpritesWithFallbackAsync(
        MediaInfo media,
        SpriteSet layout,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        FfmpegPassStats pass1 = await RunFfmpegPassAsync(
            media, layout, useHwAccel: true, progress, ct).ConfigureAwait(false);

        if (IsPass1Acceptable(pass1, layout, out string reason))
        {
            return pass1;
        }

        _logger.Warn(
            $"FFmpeg vignettes avec -hwaccel auto rejete ({reason}). Nouvelle tentative en decodage logiciel CPU. Stderr : {pass1.StderrTail}");

        SheetsReset?.Invoke(this, EventArgs.Empty);
        progress?.Report(0.0);

        FfmpegPassStats pass2 = await RunFfmpegPassAsync(
            media, layout, useHwAccel: false, progress, ct).ConfigureAwait(false);

        if (pass2.ExitCode != 0 || pass2.SheetsProduced == 0)
        {
            throw new InvalidOperationException(
                $"FFmpeg a echoue (code {pass2.ExitCode}) lors de la generation des vignettes. Dernieres lignes stderr : {pass2.StderrTail}");
        }

        return pass2;
    }

    private static bool IsPass1Acceptable(FfmpegPassStats pass, SpriteSet layout, out string reason)
    {
        if (pass.ExitCode != 0 || pass.SheetsProduced == 0)
        {
            reason = $"code {pass.ExitCode}, {pass.SheetsProduced} feuille(s)";
            return false;
        }

        if (pass.SheetsProduced < layout.SheetCount)
        {
            reason = $"feuilles incompletes ({pass.SheetsProduced}/{layout.SheetCount})";
            return false;
        }

        if (pass.DecodedFrames > 0 && pass.DecodedFrames < Math.Max(1, layout.Count - 2))
        {
            reason = $"images decodees incompletes ({pass.DecodedFrames}/{layout.Count})";
            return false;
        }

        if (pass.NonBlackFrames > 0
            && pass.TrailingBlackFrames >= 5
            && pass.TrailingBlackFrames >= pass.DecodedFrames / 3)
        {
            reason = $"suite d'images noires suspecte ({pass.TrailingBlackFrames}/{pass.DecodedFrames})";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private async Task<FfmpegPassStats> RunFfmpegPassAsync(
        MediaInfo media,
        SpriteSet layout,
        bool useHwAccel,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        PrepareEmptyDirectory(layout.Directory);

        string[] args = BuildFfmpegArguments(media, layout, useHwAccel);
        var stderrLines = new Queue<string>(MaxStderrLines + 1);
        object stderrLock = new();
        double totalSeconds = Math.Max(0.1, media.Duration.TotalSeconds);
        double totalMicros = totalSeconds * 1_000_000.0;

        int decodedFrames = 0;
        int nonBlackFrames = 0;
        int trailingBlackFrames = 0;

        int emittedSheets = 0;
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Task pollingTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(300));
            try
            {
                while (await timer.WaitForNextTickAsync(pollCts.Token).ConfigureAwait(false))
                {
                    while (!ct.IsCancellationRequested && File.Exists(GetSheetPath(layout.Directory, emittedSheets + 1)))
                    {
                        SheetReady?.Invoke(this, emittedSheets);
                        emittedSheets++;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Fin normale du sondage a l'arret du processus FFmpeg.
            }
        }, CancellationToken.None);

        void HandleStdoutLine(string line)
        {
            if (!line.StartsWith("out_time_us=", StringComparison.Ordinal))
            {
                return;
            }

            string raw = line.Substring("out_time_us=".Length).Trim();
            if (raw.Equals("N/A", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long us) && us >= 0)
            {
                // Ignorer les valeurs gonflees du filtre tile tant qu'aucune progression showinfo n'a besoin de bornage.
                if (decodedFrames == 0)
                {
                    double fraction = Math.Clamp(us / totalMicros, 0.0, 0.99);
                    progress?.Report(fraction);
                }
            }
        }

        void HandleStderrLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            if (line.Contains("Parsed_showinfo", StringComparison.Ordinal))
            {
                if (TryParseShowInfoFrame(line, out double ptsTime, out bool isSolidBlack))
                {
                    int currentCount = Interlocked.Increment(ref decodedFrames);
                    if (isSolidBlack)
                    {
                        Interlocked.Increment(ref trailingBlackFrames);
                    }
                    else
                    {
                        Interlocked.Increment(ref nonBlackFrames);
                        Interlocked.Exchange(ref trailingBlackFrames, 0);
                    }

                    double frameFraction = layout.Count > 0 ? currentCount / (double)layout.Count : 0.0;
                    double timeFraction = ptsTime > 0 ? ptsTime / totalSeconds : frameFraction;
                    double fraction = Math.Clamp(Math.Max(frameFraction, timeFraction), 0.0, 0.99);
                    progress?.Report(fraction);
                }

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

        CommandResult result;
        try
        {
            result = await Cli.Wrap(_ffmpegPath)
                .WithArguments(args)
                .WithValidation(CommandResultValidation.None)
                .WithStandardOutputPipe(PipeTarget.ToDelegate(HandleStdoutLine))
                .WithStandardErrorPipe(PipeTarget.ToDelegate(HandleStderrLine))
                .ExecuteAsync(ct, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            await pollCts.CancelAsync().ConfigureAwait(false);
            await pollingTask.ConfigureAwait(false);
        }

        int finalSheetCount = 0;
        while (File.Exists(GetSheetPath(layout.Directory, finalSheetCount)))
        {
            finalSheetCount++;
        }

        if (result.ExitCode == 0 && !ct.IsCancellationRequested)
        {
            for (int i = emittedSheets; i < finalSheetCount; i++)
            {
                SheetReady?.Invoke(this, i);
            }
        }

        string stderrTail;
        lock (stderrLock)
        {
            stderrTail = string.Join(" | ", stderrLines);
        }

        return new FfmpegPassStats(
            ExitCode: result.ExitCode,
            SheetsProduced: finalSheetCount,
            DecodedFrames: Volatile.Read(ref decodedFrames),
            NonBlackFrames: Volatile.Read(ref nonBlackFrames),
            TrailingBlackFrames: Volatile.Read(ref trailingBlackFrames),
            StderrTail: stderrTail);
    }

    private static bool TryParseShowInfoFrame(string line, out double ptsTime, out bool isSolidBlack)
    {
        ptsTime = 0.0;
        isSolidBlack = false;

        int meanMarkerIndex = line.IndexOf("mean:[", StringComparison.Ordinal);
        if (meanMarkerIndex < 0)
        {
            return false;
        }

        int ptsIdx = line.IndexOf("pts_time:", StringComparison.Ordinal);
        if (ptsIdx >= 0)
        {
            ReadOnlySpan<char> ptsSpan = line.AsSpan(ptsIdx + "pts_time:".Length).TrimStart();
            int endIdx = ptsSpan.IndexOf(' ');
            if (endIdx > 0)
            {
                ptsSpan = ptsSpan[..endIdx];
            }

            if (double.TryParse(ptsSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedPts)
                && double.IsFinite(parsedPts)
                && parsedPts >= 0)
            {
                ptsTime = parsedPts;
            }
        }

        if (!TryParseFirstBracketFloat(line.AsSpan(meanMarkerIndex + "mean:[".Length), out double meanY))
        {
            return false;
        }

        int stdevMarkerIndex = line.IndexOf("stdev:[", meanMarkerIndex, StringComparison.Ordinal);
        if (stdevMarkerIndex < 0)
        {
            return false;
        }

        if (!TryParseFirstBracketFloat(line.AsSpan(stdevMarkerIndex + "stdev:[".Length), out double stdevY))
        {
            return false;
        }

        isSolidBlack = meanY <= 18.0 && stdevY <= 2.0;
        return true;
    }

    private static bool TryParseFirstBracketFloat(ReadOnlySpan<char> span, out double value)
    {
        value = 0.0;
        span = span.TrimStart();
        int sepIdx = span.IndexOfAny(' ', ']', '\t');
        if (sepIdx <= 0)
        {
            return false;
        }

        return double.TryParse(span[..sepIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);
    }

    private static string[] BuildFfmpegArguments(MediaInfo media, SpriteSet layout, bool useHwAccel)
    {
        var args = new List<string>(24)
        {
            "-hide_banner",
            "-y",
            "-fflags",
            useHwAccel ? "+discardcorrupt" : "+genpts+discardcorrupt",
            "-err_detect",
            "ignore_err"
        };

        if (media.Duration.TotalSeconds > 600.0)
        {
            args.Add("-skip_frame");
            args.Add("nokey");
        }

        if (useHwAccel)
        {
            args.Add("-hwaccel");
            args.Add("auto");
        }
        else
        {
            args.Add("-threads");
            args.Add("2");
        }

        string intervalText = ((int)layout.IntervalSeconds).ToString(CultureInfo.InvariantCulture);
        string outputPattern = Path.Combine(layout.Directory, "sprite_%03d.jpg");

        args.Add("-i");
        args.Add(media.Path);
        args.Add("-an");
        args.Add("-sn");
        args.Add("-dn");
        args.Add("-vf");
        args.Add($"fps=1/{intervalText},scale=160:-2,showinfo,tile=10x10:color=0x22262E");
        args.Add("-q:v");
        args.Add("5");
        args.Add("-start_number");
        args.Add("0");
        args.Add("-progress");
        args.Add("pipe:1");
        args.Add("-nostats");
        args.Add(outputPattern);

        return args.ToArray();
    }

    private void PrepareEmptyDirectory(string directory)
    {
        CleanupSpriteFiles(directory);
        _cacheService.TouchCacheDirectory(directory);
    }

    private static async Task WriteMetadataAsync(MediaInfo media, SpriteSet layout, CancellationToken ct)
    {
        var meta = new SpriteMetaJson
        {
            Version = CurrentMetaVersion,
            Interval = layout.IntervalSeconds,
            ThumbWidth = layout.ThumbWidth,
            ThumbHeight = layout.ThumbHeight,
            Cols = layout.Columns,
            Rows = layout.Rows,
            Count = layout.Count,
            SheetCount = layout.SheetCount,
            ValidFrameCount = layout.ValidFrameCount > 0 ? layout.ValidFrameCount : layout.Count,
            Duration = media.Duration.TotalSeconds,
            Complete = true
        };

        string metaPath = Path.Combine(layout.Directory, "meta.json");
        string json = JsonSerializer.Serialize(meta, JsonOptions);
        await File.WriteAllTextAsync(metaPath, json, ct).ConfigureAwait(false);
    }

    private static string GetSheetPath(string directory, int index)
        => Path.Combine(directory, $"sprite_{index:D3}.jpg");

    private void CleanupSpriteFiles(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(directory, "sprite_*.jpg"))
            {
                File.Delete(file);
            }

            string metaPath = Path.Combine(directory, "meta.json");
            if (File.Exists(metaPath))
            {
                File.Delete(metaPath);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Impossible de nettoyer les vignettes incompletes dans {directory} : {ex.Message}");
        }
    }
}
