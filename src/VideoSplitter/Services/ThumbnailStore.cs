using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VideoSplitter.Models;

namespace VideoSplitter.Services;

public sealed class ThumbnailStore : IThumbnailProvider
{
    private const int MaxCachedBrushes = 512;

    private readonly SpriteSet _layout;
    private readonly IThumbnailService _thumbnailService;
    private readonly IAppLogger _logger;
    private readonly Dispatcher _dispatcher;
    private readonly BitmapSource?[] _sheets;

    private readonly Dictionary<int, ImageBrush> _brushCache = new(MaxCachedBrushes);
    private readonly Queue<int> _brushOrder = new(MaxCachedBrushes);

    private int _loadedSheetCount;
    private int _lastIndex = -1;
    private CroppedBitmap? _lastCrop;
    private bool _disposed;

    public SpriteSet Layout => _layout;
    public bool IsReady => Volatile.Read(ref _loadedSheetCount) > 0;

    public event EventHandler? ThumbnailsChanged;

    public ThumbnailStore(SpriteSet layout, IThumbnailService thumbnailService, IAppLogger logger)
    {
        _layout = layout;
        _thumbnailService = thumbnailService;
        _logger = logger;
        _dispatcher = Application.Current.Dispatcher;
        _sheets = new BitmapSource?[Math.Max(1, layout.SheetCount)];

        _thumbnailService.SheetReady += OnSheetReady;
    }

    private void OnSheetReady(object? sender, int sheetIndex)
    {
        if (_disposed || sheetIndex < 0 || sheetIndex >= _sheets.Length)
        {
            return;
        }

        _ = Task.Run(() => LoadSheet(sheetIndex));
    }

    private void LoadSheet(int sheetIndex)
    {
        if (_disposed || Volatile.Read(ref _sheets[sheetIndex]) is not null)
        {
            return;
        }

        string path = Path.Combine(_layout.Directory, $"sprite_{sheetIndex:D3}.jpg");
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            if (!_disposed)
            {
                Volatile.Write(ref _sheets[sheetIndex], bitmap);
                Interlocked.Increment(ref _loadedSheetCount);
                _dispatcher.BeginInvoke(() =>
                {
                    if (!_disposed)
                    {
                        ThumbnailsChanged?.Invoke(this, EventArgs.Empty);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Impossible de charger la feuille de vignettes {path} : {ex.Message}");
        }
    }

    public int IndexForTime(TimeSpan time)
    {
        if (_layout.Count <= 0 || _layout.IntervalSeconds <= 0)
        {
            return 0;
        }

        int rawIndex = (int)Math.Floor(time.TotalSeconds / _layout.IntervalSeconds);
        return Math.Clamp(rawIndex, 0, _layout.Count - 1);
    }

    public ImageSource? GetThumbnail(TimeSpan time)
    {
        if (_disposed || _layout.Count <= 0 || _layout.IntervalSeconds <= 0)
        {
            return null;
        }

        int index = IndexForTime(time);

        if (index == _lastIndex && _lastCrop is not null)
        {
            return _lastCrop;
        }

        int perSheet = _layout.Columns * _layout.Rows;
        if (perSheet <= 0)
        {
            return null;
        }

        int sheetIndex = index / perSheet;
        if (sheetIndex < 0 || sheetIndex >= _sheets.Length)
        {
            return null;
        }

        BitmapSource? sheet = Volatile.Read(ref _sheets[sheetIndex]);
        if (sheet is null)
        {
            return null;
        }

        int cell = index % perSheet;
        int col = cell % _layout.Columns;
        int row = cell / _layout.Columns;

        int x = col * _layout.ThumbWidth;
        int y = row * _layout.ThumbHeight;
        if (x + _layout.ThumbWidth > sheet.PixelWidth || y + _layout.ThumbHeight > sheet.PixelHeight)
        {
            return null;
        }

        var crop = new CroppedBitmap(sheet, new Int32Rect(x, y, _layout.ThumbWidth, _layout.ThumbHeight));
        crop.Freeze();

        _lastIndex = index;
        _lastCrop = crop;
        return crop;
    }

    public Brush? GetThumbnailBrush(int index)
    {
        if (_disposed || _layout.Count <= 0 || _layout.Columns <= 0 || _layout.Rows <= 0)
        {
            return null;
        }

        index = Math.Clamp(index, 0, _layout.Count - 1);

        if (_brushCache.TryGetValue(index, out ImageBrush? cached))
        {
            return cached;
        }

        int perSheet = _layout.Columns * _layout.Rows;
        int sheetIndex = index / perSheet;
        if (sheetIndex < 0 || sheetIndex >= _sheets.Length)
        {
            return null;
        }

        BitmapSource? sheet = Volatile.Read(ref _sheets[sheetIndex]);
        if (sheet is null)
        {
            return null;
        }

        int cell = index % perSheet;
        int col = cell % _layout.Columns;
        int row = cell / _layout.Columns;

        double vx = col / (double)_layout.Columns;
        double vy = row / (double)_layout.Rows;
        double vw = 1.0 / _layout.Columns;
        double vh = 1.0 / _layout.Rows;

        var brush = new ImageBrush(sheet)
        {
            Viewbox = new Rect(vx, vy, vw, vh),
            ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            Stretch = Stretch.Fill
        };
        brush.Freeze();

        if (_brushCache.Count >= MaxCachedBrushes && _brushOrder.TryDequeue(out int oldestIndex))
        {
            _brushCache.Remove(oldestIndex);
        }

        _brushCache[index] = brush;
        _brushOrder.Enqueue(index);
        return brush;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _thumbnailService.SheetReady -= OnSheetReady;
        _lastCrop = null;
        _brushCache.Clear();
        _brushOrder.Clear();
        Array.Clear(_sheets);
    }
}
