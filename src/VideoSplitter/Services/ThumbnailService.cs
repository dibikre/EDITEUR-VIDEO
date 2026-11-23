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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ICacheService _cacheService;
    private readonly IAppLogger _logger;
    private readonly string _ffmpegPath;

    public event EventHandler<int>? SheetReady;

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

        int count = Math.Max(1, (int)Math.Floor(durationSeconds / intervalSeconds) + 1);
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
            SheetCount: sheetCount);
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
            int actualSheetCount = await GenerateSpritesWithFallbackAsync(media, layout, progress, ct).ConfigureAwait(false);
            SpriteSet finalLayout = layout with { SheetCount = actualSheetCount };

            await WriteMetadataAsync(media, finalLayout, ct).ConfigureAwait(false);
            progress?.Report(1.0);

            stopwatch.Stop();
            _logger.Info(
                $"Vignettes generees en {stopwatch.Elapsed.TotalSeconds:F2} s ({actualSheetCount} feuille(s), intervalle {finalLayout.IntervalSeconds:0} s) : {finalLayout.Directory}");

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
            if (meta is null || !meta.Complete || meta.Count <= 0 || meta.Cols <= 0 || meta.Rows <= 0)
            {
                return false;
            }

            int expectedSheets = meta.SheetCount > 0
                ? meta.SheetCount
                : Math.Max(1, (int)Math.Ceiling(meta.Count / (double)(meta.Cols * meta.Rows)));

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
                ThumbWidth: meta.ThumbWidth > 0 ? meta.ThumbWidth : expectedLayout.ThumbWidth,
                ThumbHeight: meta.ThumbHeight > 0 ? meta.ThumbHeight : expectedLayout.ThumbHeight,
                Columns: meta.Cols,
                Rows: meta.Rows,
                Count: meta.Count,
                SheetCount: expectedSheets);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"meta.json invalide dans {expectedLayout.Directory} : {ex.Message}");
            return false;
        }
    }

    private async Task<int> GenerateSpritesWithFallbackAsync(
        MediaInfo media,
        SpriteSet layout,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        (int exitCode, int sheetsProduced, string stderrTail) = await RunFfmpegPassAsync(
            media, layout, useHwAccel: true, progress, ct).ConfigureAwait(false);

        if (exitCode == 0 && sheetsProduced > 0)
        {
            return sheetsProduced;
        }

        _logger.Warn(
            $"FFmpeg vignettes avec -hwaccel auto a echoue (code {exitCode}). Nouvelle tentative sans acceleration materielle. Stderr : {stderrTail}");

        (exitCode, sheetsProduced, stderrTail) = await RunFfmpegPassAsync(
            media, layout, useHwAccel: false, progress, ct).ConfigureAwait(false);

        if (exitCode != 0 || sheetsProduced == 0)
        {
            throw new InvalidOperationException(
                $"FFmpeg a echoue (code {exitCode}) lors de la generation des vignettes. Dernieres lignes stderr : {stderrTail}");
        }

        return sheetsProduced;
    }

    private async Task<(int ExitCode, int SheetsProduced, string StderrTail)> RunFfmpegPassAsync(
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
        double totalMicros = Math.Max(1.0, media.Duration.TotalSeconds * 1_000_000.0);

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
                double fraction = Math.Clamp(us / totalMicros, 0.0, 1.0);
                progress?.Report(fraction);
            }
        }

        void HandleStderrLine(string line)
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

        return (result.ExitCode, finalSheetCount, stderrTail);
    }

    private static string[] BuildFfmpegArguments(MediaInfo media, SpriteSet layout, bool useHwAccel)
    {
        var args = new List<string>(20)
        {
            "-hide_banner",
            "-y"
        };

        if (media.Duration.TotalSeconds > 600.0)
        {
            args.Add("-skip_frame");
            args.Add("nokey");
        }

        args.Add("-threads");
        args.Add("2");

        if (useHwAccel)
        {
            args.Add("-hwaccel");
            args.Add("auto");
        }

        string intervalText = ((int)layout.IntervalSeconds).ToString(CultureInfo.InvariantCulture);
        string outputPattern = Path.Combine(layout.Directory, "sprite_%03d.jpg");

        args.Add("-i");
        args.Add(media.Path);
        args.Add("-an");
        args.Add("-sn");
        args.Add("-dn");
        args.Add("-vf");
        args.Add($"fps=1/{intervalText},scale=160:-2,tile=10x10");
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
            Interval = layout.IntervalSeconds,
            ThumbWidth = layout.ThumbWidth,
            ThumbHeight = layout.ThumbHeight,
            Cols = layout.Columns,
            Rows = layout.Rows,
            Count = layout.Count,
            SheetCount = layout.SheetCount,
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
