using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CliWrap;
using VideoSplitter.Models;

namespace VideoSplitter.Services;

public interface IRangeThumbnailProvider : IThumbnailProvider
{
    double RangeStartSeconds { get; }
    double RangeEndSeconds { get; }
    bool ContainsTime(double seconds);
}

public interface IRangeThumbnailService
{
    Task<IRangeThumbnailProvider?> ExtractRangeAsync(
        MediaInfo media,
        double startSeconds,
        double endSeconds,
        double viewportWidth,
        CancellationToken ct);
}

public sealed class RangeThumbnailService : IRangeThumbnailService
{
    private const int ThumbWidth = 200;
    private const int GridColumns = 8;
    private const int MinTileCount = 10;
    private const int MaxTileCount = 32;

    private readonly IAppLogger _logger;
    private readonly string _ffmpegPath;

    public RangeThumbnailService(IAppLogger logger)
    {
        _logger = logger;
        _ffmpegPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ffmpeg", "ffmpeg.exe");
    }

    public async Task<IRangeThumbnailProvider?> ExtractRangeAsync(
        MediaInfo media,
        double startSeconds,
        double endSeconds,
        double viewportWidth,
        CancellationToken ct)
    {
        if (!File.Exists(_ffmpegPath))
        {
            return null;
        }

        double totalSec = Math.Max(0.1, media.Duration.TotalSeconds);
        double clampedStart = Math.Clamp(startSeconds, 0.0, totalSec);
        double clampedEnd = Math.Clamp(endSeconds, clampedStart + 0.1, totalSec);
        double span = Math.Max(0.1, clampedEnd - clampedStart);

        int rawHeight = media.Width > 0 && media.Height > 0
            ? (int)Math.Round(ThumbWidth * (double)media.Height / media.Width)
            : 112;
        int thumbHeight = Math.Max(2, rawHeight % 2 == 0 ? rawHeight : rawHeight + 1);

        double approxTileWidth = 80.0 * ThumbWidth / thumbHeight;
        int tileCount = Math.Clamp(
            (int)Math.Ceiling(Math.Max(400.0, viewportWidth) / Math.Max(20.0, approxTileWidth)) + 2,
            MinTileCount,
            MaxTileCount);

        double intervalSeconds = span / tileCount;
        int cols = Math.Min(GridColumns, tileCount);
        int rows = Math.Max(1, (int)Math.Ceiling(tileCount / (double)cols));

        string tempDir = Path.Combine(Path.GetTempPath(), "VideoSplitter");
        Directory.CreateDirectory(tempDir);
        string tempFile = Path.Combine(tempDir, $"range_{Guid.NewGuid():N}.jpg");

        try
        {
            string[] args = BuildArgs(media.Path, clampedStart, span, intervalSeconds, tileCount, cols, rows, tempFile);
            int decodedCount = 0;

            void HandleStderr(string line)
            {
                if (line.Contains("Parsed_showinfo", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref decodedCount);
                }
            }

            CommandResult result = await Cli.Wrap(_ffmpegPath)
                .WithArguments(args)
                .WithValidation(CommandResultValidation.None)
                .WithStandardErrorPipe(PipeTarget.ToDelegate(HandleStderr))
                .ExecuteAsync(ct, CancellationToken.None)
                .ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            if (result.ExitCode != 0 || !File.Exists(tempFile))
            {
                return null;
            }

            var fi = new FileInfo(tempFile);
            if (fi.Length <= 0)
            {
                return null;
            }

            byte[] imageBytes = await File.ReadAllBytesAsync(tempFile, ct).ConfigureAwait(false);
            using var ms = new MemoryStream(imageBytes, writable: false);
            var bitmap = new Bitmap(ms);

            int actualThumbWidth = Math.Max(2, bitmap.PixelSize.Width / cols);
            int actualThumbHeight = Math.Max(2, bitmap.PixelSize.Height / rows);
            int validFrames = decodedCount > 0 ? Math.Min(decodedCount, tileCount) : tileCount;
            var layout = new SpriteSet(
                Directory: string.Empty,
                IntervalSeconds: intervalSeconds,
                ThumbWidth: actualThumbWidth,
                ThumbHeight: actualThumbHeight,
                Columns: cols,
                Rows: rows,
                Count: tileCount,
                SheetCount: 1,
                ValidFrameCount: validFrames);

            return new RangeThumbnailStrip(layout, bitmap, clampedStart, clampedEnd);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Vignettes de zoom indisponibles ({clampedStart:F2}s - {clampedEnd:F2}s) : {ex.Message}");
            return null;
        }
        finally
        {
            TryDeleteFile(tempFile);
        }
    }

    private static string[] BuildArgs(
        string mediaPath,
        double startSeconds,
        double spanSeconds,
        double intervalSeconds,
        int tileCount,
        int cols,
        int rows,
        string outputPath)
    {
        var args = new List<string>(24)
        {
            "-hide_banner",
            "-y",
            "-fflags", "+genpts+discardcorrupt",
            "-err_detect", "ignore_err",
            "-threads", "2"
        };

        if (spanSeconds > 90.0 && intervalSeconds >= 2.0)
        {
            args.Add("-skip_frame");
            args.Add("nokey");
        }

        double readDuration = spanSeconds + Math.Min(1.0, intervalSeconds * 0.5);
        string ssText = startSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        string tText = readDuration.ToString("0.###", CultureInfo.InvariantCulture);
        string fpsExpr = $"{tileCount.ToString(CultureInfo.InvariantCulture)}/{spanSeconds.ToString("0.######", CultureInfo.InvariantCulture)}";

        args.Add("-ss");
        args.Add(ssText);
        args.Add("-t");
        args.Add(tText);
        args.Add("-i");
        args.Add(mediaPath);
        args.Add("-an");
        args.Add("-sn");
        args.Add("-dn");
        args.Add("-vf");
        args.Add($"fps={fpsExpr},scale={ThumbWidth}:-2,showinfo,tile={cols}x{rows}:color=0x22262E");
        args.Add("-frames:v");
        args.Add("1");
        args.Add("-q:v");
        args.Add("4");
        args.Add(outputPath);

        return args.ToArray();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignorer si le fichier temporaire est deja supprime.
        }
    }
}

public sealed class RangeThumbnailStrip : IRangeThumbnailProvider
{
    private Bitmap? _sheet;
    private int _lastIndex = -1;
    private CroppedBitmap? _lastCrop;
    private bool _disposed;

    public SpriteSet Layout { get; }
    public double RangeStartSeconds { get; }
    public double RangeEndSeconds { get; }
    public bool IsReady => !_disposed && _sheet is not null;

    public event EventHandler? ThumbnailsChanged
    {
        add { }
        remove { }
    }

    public RangeThumbnailStrip(SpriteSet layout, Bitmap sheet, double rangeStartSeconds, double rangeEndSeconds)
    {
        Layout = layout;
        _sheet = sheet;
        RangeStartSeconds = rangeStartSeconds;
        RangeEndSeconds = rangeEndSeconds;
    }

    public bool ContainsTime(double seconds)
        => !_disposed && seconds >= RangeStartSeconds - 0.05 && seconds <= RangeEndSeconds + 0.05;

    public int IndexForTime(TimeSpan time)
    {
        if (Layout.Count <= 0 || Layout.IntervalSeconds <= 0)
        {
            return 0;
        }

        double rel = Math.Max(0.0, time.TotalSeconds - RangeStartSeconds);
        int rawIndex = (int)Math.Floor(rel / Layout.IntervalSeconds);
        int validLimit = Math.Max(1, Math.Min(Layout.ValidFrameCount, Layout.Count));
        return Math.Clamp(rawIndex, 0, validLimit - 1);
    }

    public IImage? GetThumbnail(TimeSpan time)
    {
        if (_disposed || _sheet is null || Layout.Count <= 0)
        {
            return null;
        }

        int index = IndexForTime(time);
        if (index == _lastIndex && _lastCrop is not null)
        {
            return _lastCrop;
        }

        if (!TryGetTilePixelRect(index, _sheet, out PixelRect pixelRect))
        {
            return null;
        }

        _lastCrop = new CroppedBitmap(_sheet, pixelRect);
        _lastIndex = index;
        return _lastCrop;
    }

    public bool TryGetThumbnailTile(int index, out IImage? sheet, out Rect sourceRect)
    {
        sheet = null;
        sourceRect = default;

        if (_disposed || _sheet is null || Layout.Count <= 0)
        {
            return false;
        }

        int validLimit = Math.Max(1, Math.Min(Layout.ValidFrameCount, Layout.Count));
        int clamped = Math.Clamp(index, 0, validLimit - 1);
        if (!TryGetTilePixelRect(clamped, _sheet, out PixelRect rect))
        {
            return false;
        }

        sheet = _sheet;
        sourceRect = new Rect(rect.X, rect.Y, rect.Width, rect.Height);
        return true;
    }

    private bool TryGetTilePixelRect(int index, Bitmap sheet, out PixelRect rect)
    {
        rect = default;
        if (Layout.Columns <= 0 || Layout.Rows <= 0)
        {
            return false;
        }

        int col = index % Layout.Columns;
        int row = index / Layout.Columns;
        int x = col * Layout.ThumbWidth;
        int y = row * Layout.ThumbHeight;
        if (x + Layout.ThumbWidth > sheet.PixelSize.Width || y + Layout.ThumbHeight > sheet.PixelSize.Height)
        {
            return false;
        }

        rect = new PixelRect(x, y, Layout.ThumbWidth, Layout.ThumbHeight);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lastCrop = null;
        Bitmap? sheet = Interlocked.Exchange(ref _sheet, null);
        if (sheet is null)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            sheet.Dispose();
        }
        else
        {
            Dispatcher.UIThread.Post(sheet.Dispose);
        }
    }
}
