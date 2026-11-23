using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using VideoSplitter.Models;

namespace VideoSplitter.Services;

public sealed class ThumbnailStore : IThumbnailProvider
{
    private readonly IThumbnailService _thumbnailService;
    private readonly IAppLogger _logger;
    private readonly object _sheetsLock = new();

    private SpriteSet _layout;
    private Bitmap?[] _sheets;
    private int _loadedSheetCount;
    private int _sheetGeneration;
    private int _lastIndex = -1;
    private CroppedBitmap? _lastCrop;
    private bool _disposed;

    public SpriteSet Layout => Volatile.Read(ref _layout);
    public bool IsReady => Volatile.Read(ref _loadedSheetCount) > 0;

    public event EventHandler? ThumbnailsChanged;

    public ThumbnailStore(SpriteSet layout, IThumbnailService thumbnailService, IAppLogger logger)
    {
        _layout = layout;
        _thumbnailService = thumbnailService;
        _logger = logger;
        _sheets = new Bitmap?[Math.Max(1, layout.SheetCount)];

        _thumbnailService.SheetReady += OnSheetReady;
        _thumbnailService.SheetsReset += OnSheetsReset;
    }

    public void UpdateLayout(SpriteSet finalLayout, bool reloadMissingOrUpdated = true)
    {
        if (_disposed)
        {
            return;
        }

        int newLength = Math.Max(1, finalLayout.SheetCount);
        List<Bitmap>? toDispose = null;

        lock (_sheetsLock)
        {
            if (_disposed)
            {
                return;
            }

            Volatile.Write(ref _layout, finalLayout);

            if (_sheets.Length != newLength)
            {
                var resized = new Bitmap?[newLength];
                int copyLen = Math.Min(_sheets.Length, newLength);
                for (int i = 0; i < copyLen; i++)
                {
                    resized[i] = _sheets[i];
                }

                for (int i = newLength; i < _sheets.Length; i++)
                {
                    if (_sheets[i] is { } extra)
                    {
                        toDispose ??= [];
                        toDispose.Add(extra);
                    }
                }

                _sheets = resized;
                int loaded = 0;
                for (int i = 0; i < _sheets.Length; i++)
                {
                    if (_sheets[i] is not null)
                    {
                        loaded++;
                    }
                }

                Volatile.Write(ref _loadedSheetCount, loaded);
            }

            _lastIndex = -1;
            _lastCrop = null;
        }

        DisposeOnUiThread(toDispose);

        if (reloadMissingOrUpdated)
        {
            int gen = Volatile.Read(ref _sheetGeneration);
            for (int i = 0; i < newLength; i++)
            {
                int idx = i;
                _ = Task.Run(() => LoadSheet(idx, forceReload: false, expectedGeneration: gen));
            }
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                ThumbnailsChanged?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    private void OnSheetsReset(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        List<Bitmap>? toDispose = null;
        lock (_sheetsLock)
        {
            if (_disposed)
            {
                return;
            }

            Interlocked.Increment(ref _sheetGeneration);

            for (int i = 0; i < _sheets.Length; i++)
            {
                if (_sheets[i] is { } existing)
                {
                    toDispose ??= [];
                    toDispose.Add(existing);
                    _sheets[i] = null;
                }
            }

            Volatile.Write(ref _loadedSheetCount, 0);
            _lastIndex = -1;
            _lastCrop = null;
        }

        DisposeOnUiThread(toDispose);
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                ThumbnailsChanged?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    private void OnSheetReady(object? sender, int sheetIndex)
    {
        if (_disposed)
        {
            return;
        }

        int gen = Volatile.Read(ref _sheetGeneration);
        _ = Task.Run(() => LoadSheet(sheetIndex, forceReload: true, expectedGeneration: gen));
    }

    private void LoadSheet(int sheetIndex, bool forceReload, int expectedGeneration)
    {
        SpriteSet layout = Layout;
        lock (_sheetsLock)
        {
            if (_disposed
                || expectedGeneration != Volatile.Read(ref _sheetGeneration)
                || sheetIndex < 0
                || sheetIndex >= _sheets.Length)
            {
                return;
            }

            if (!forceReload && _sheets[sheetIndex] is not null)
            {
                return;
            }
        }

        string path = Path.Combine(layout.Directory, $"sprite_{sheetIndex:D3}.jpg");
        if (!File.Exists(path))
        {
            return;
        }

        Bitmap? bitmap = null;
        Bitmap? previous = null;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            bitmap = new Bitmap(stream);

            lock (_sheetsLock)
            {
                if (_disposed
                    || expectedGeneration != Volatile.Read(ref _sheetGeneration)
                    || sheetIndex < 0
                    || sheetIndex >= _sheets.Length)
                {
                    bitmap.Dispose();
                    return;
                }

                previous = _sheets[sheetIndex];
                _sheets[sheetIndex] = bitmap;
                if (previous is null)
                {
                    Interlocked.Increment(ref _loadedSheetCount);
                }

                _lastIndex = -1;
                _lastCrop = null;
            }

            if (previous is not null)
            {
                DisposeOnUiThread([previous]);
            }

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
            bitmap?.Dispose();
            _logger.Warn($"Impossible de charger la feuille de vignettes {path} : {ex.Message}");
        }
    }

    public int IndexForTime(TimeSpan time)
    {
        SpriteSet layout = Layout;
        if (layout.Count <= 0 || layout.IntervalSeconds <= 0)
        {
            return 0;
        }

        int rawIndex = (int)Math.Floor(time.TotalSeconds / layout.IntervalSeconds);
        return Math.Clamp(rawIndex, 0, layout.Count - 1);
    }

    public IImage? GetThumbnail(TimeSpan time)
    {
        SpriteSet layout = Layout;
        if (_disposed || layout.Count <= 0 || layout.IntervalSeconds <= 0)
        {
            return null;
        }

        int index = IndexForTime(time);
        int validLimit = GetValidLimit(layout);
        if (validLimit <= 0 || (validLimit < layout.Count - 1 && index >= validLimit))
        {
            return null;
        }

        index = Math.Clamp(index, 0, validLimit - 1);

        if (index == _lastIndex && _lastCrop is not null)
        {
            return _lastCrop;
        }

        int perSheet = layout.Columns * layout.Rows;
        if (perSheet <= 0)
        {
            return null;
        }

        int sheetIndex = index / perSheet;
        Bitmap? sheet = GetSheetSnapshot(sheetIndex);
        if (sheet is null)
        {
            return null;
        }

        int cell = index % perSheet;
        int col = cell % layout.Columns;
        int row = cell / layout.Columns;

        int x = col * layout.ThumbWidth;
        int y = row * layout.ThumbHeight;
        if (x + layout.ThumbWidth > sheet.PixelSize.Width || y + layout.ThumbHeight > sheet.PixelSize.Height)
        {
            return null;
        }

        var crop = new CroppedBitmap(sheet, new PixelRect(x, y, layout.ThumbWidth, layout.ThumbHeight));
        _lastIndex = index;
        _lastCrop = crop;
        return crop;
    }

    public bool TryGetThumbnailTile(int index, out IImage? sheet, out Rect sourceRect)
    {
        sheet = null;
        sourceRect = default;

        SpriteSet layout = Layout;
        if (_disposed || layout.Count <= 0 || layout.Columns <= 0 || layout.Rows <= 0)
        {
            return false;
        }

        int validLimit = GetValidLimit(layout);
        if (validLimit <= 0 || (validLimit < layout.Count - 1 && index >= validLimit))
        {
            return false;
        }

        index = Math.Clamp(index, 0, validLimit - 1);

        int perSheet = layout.Columns * layout.Rows;
        int sheetIndex = index / perSheet;
        Bitmap? bitmap = GetSheetSnapshot(sheetIndex);
        if (bitmap is null)
        {
            return false;
        }

        int cell = index % perSheet;
        int col = cell % layout.Columns;
        int row = cell / layout.Columns;

        int x = col * layout.ThumbWidth;
        int y = row * layout.ThumbHeight;
        if (x + layout.ThumbWidth > bitmap.PixelSize.Width || y + layout.ThumbHeight > bitmap.PixelSize.Height)
        {
            return false;
        }

        sheet = bitmap;
        sourceRect = new Rect(x, y, layout.ThumbWidth, layout.ThumbHeight);
        return true;
    }

    private Bitmap? GetSheetSnapshot(int sheetIndex)
    {
        lock (_sheetsLock)
        {
            if (_disposed || sheetIndex < 0 || sheetIndex >= _sheets.Length)
            {
                return null;
            }

            return _sheets[sheetIndex];
        }
    }

    private static int GetValidLimit(SpriteSet layout)
        => layout.ValidFrameCount > 0 ? Math.Min(layout.ValidFrameCount, layout.Count) : layout.Count;

    private static void DisposeOnUiThread(List<Bitmap>? bitmaps)
    {
        if (bitmaps is null || bitmaps.Count == 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            foreach (Bitmap bmp in bitmaps)
            {
                bmp.Dispose();
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _thumbnailService.SheetReady -= OnSheetReady;
        _thumbnailService.SheetsReset -= OnSheetsReset;

        List<Bitmap>? toDispose = null;
        lock (_sheetsLock)
        {
            _lastCrop = null;
            for (int i = 0; i < _sheets.Length; i++)
            {
                if (_sheets[i] is { } sheet)
                {
                    toDispose ??= [];
                    toDispose.Add(sheet);
                    _sheets[i] = null;
                }
            }
        }

        if (toDispose is not null)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                foreach (Bitmap bmp in toDispose)
                {
                    bmp.Dispose();
                }
            }
            else
            {
                DisposeOnUiThread(toDispose);
            }
        }
    }
}
