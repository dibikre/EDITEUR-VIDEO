using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VideoSplitter.Models;

namespace VideoSplitter.Services;

public sealed class ThumbnailStore : IThumbnailProvider
{
    private readonly SpriteSet _layout;
    private readonly IThumbnailService _thumbnailService;
    private readonly IAppLogger _logger;
    private readonly BitmapSource?[] _sheets;

    private int _loadedSheetCount;
    private int _lastIndex = -1;
    private CroppedBitmap? _lastCrop;
    private bool _disposed;

    public bool IsReady => Volatile.Read(ref _loadedSheetCount) > 0;

    public ThumbnailStore(SpriteSet layout, IThumbnailService thumbnailService, IAppLogger logger)
    {
        _layout = layout;
        _thumbnailService = thumbnailService;
        _logger = logger;
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
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Impossible de charger la feuille de vignettes {path} : {ex.Message}");
        }
    }

    public ImageSource? GetThumbnail(TimeSpan time)
    {
        if (_disposed || _layout.Count <= 0 || _layout.IntervalSeconds <= 0)
        {
            return null;
        }

        int rawIndex = (int)Math.Floor(time.TotalSeconds / _layout.IntervalSeconds);
        int index = Math.Clamp(rawIndex, 0, _layout.Count - 1);

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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _thumbnailService.SheetReady -= OnSheetReady;
        _lastCrop = null;
        Array.Clear(_sheets);
    }
}
