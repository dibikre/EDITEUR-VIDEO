using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using VideoSplitter.Models;

namespace VideoSplitter.Services;

public sealed class ThumbnailStore : IThumbnailProvider
{
    private readonly SpriteSet _layout;
    private readonly IThumbnailService _thumbnailService;
    private readonly IAppLogger _logger;
    private readonly Bitmap?[] _sheets;

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
        _sheets = new Bitmap?[Math.Max(1, layout.SheetCount)];

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
            var bitmap = new Bitmap(stream);

            if (_disposed)
            {
                bitmap.Dispose();
                return;
            }

            Volatile.Write(ref _sheets[sheetIndex], bitmap);
            Interlocked.Increment(ref _loadedSheetCount);
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed)
                {
                    ThumbnailsChanged?.Invoke(this, EventArgs.Empty);
                }
            });
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

    public IImage? GetThumbnail(TimeSpan time)
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

        Bitmap? sheet = Volatile.Read(ref _sheets[sheetIndex]);
        if (sheet is null)
        {
            return null;
        }

        int cell = index % perSheet;
        int col = cell % _layout.Columns;
        int row = cell / _layout.Columns;

        int x = col * _layout.ThumbWidth;
        int y = row * _layout.ThumbHeight;
        if (x + _layout.ThumbWidth > sheet.PixelSize.Width || y + _layout.ThumbHeight > sheet.PixelSize.Height)
        {
            return null;
        }

        var crop = new CroppedBitmap(sheet, new PixelRect(x, y, _layout.ThumbWidth, _layout.ThumbHeight));
        _lastIndex = index;
        _lastCrop = crop;
        return crop;
    }

    public bool TryGetThumbnailTile(int index, out IImage? sheet, out Rect sourceRect)
    {
        sheet = null;
        sourceRect = default;

        if (_disposed || _layout.Count <= 0 || _layout.Columns <= 0 || _layout.Rows <= 0)
        {
            return false;
        }

        index = Math.Clamp(index, 0, _layout.Count - 1);

        int perSheet = _layout.Columns * _layout.Rows;
        int sheetIndex = index / perSheet;
        if (sheetIndex < 0 || sheetIndex >= _sheets.Length)
        {
            return false;
        }

        Bitmap? bitmap = Volatile.Read(ref _sheets[sheetIndex]);
        if (bitmap is null)
        {
            return false;
        }

        int cell = index % perSheet;
        int col = cell % _layout.Columns;
        int row = cell / _layout.Columns;

        int x = col * _layout.ThumbWidth;
        int y = row * _layout.ThumbHeight;
        if (x + _layout.ThumbWidth > bitmap.PixelSize.Width || y + _layout.ThumbHeight > bitmap.PixelSize.Height)
        {
            return false;
        }

        sheet = bitmap;
        sourceRect = new Rect(x, y, _layout.ThumbWidth, _layout.ThumbHeight);
        return true;
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

        for (int i = 0; i < _sheets.Length; i++)
        {
            Bitmap? sheet = Interlocked.Exchange(ref _sheets[i], null);
            sheet?.Dispose();
        }
    }
}
