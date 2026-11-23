using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace VideoSplitter.Controls;

public sealed class ThumbnailHoverPopup
{
    private const double VerticalCursorGap = 10.0;
    private const double BelowCursorGap = 18.0;
    private const double HorizontalPaddingMargin = 4.0;

    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExNoActivate = 0x08000000L;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = false)]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = false)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    private static readonly SolidColorBrush PopupBackgroundBrush = CreateFrozenBrush(0x1A, 0x1D, 0x23);
    private static readonly SolidColorBrush PopupBorderBrush = CreateFrozenBrush(0x2E, 0x34, 0x40);
    private static readonly SolidColorBrush TimeTextBrush = CreateFrozenBrush(0xED, 0xF0, 0xF5);
    private static readonly FontFamily TimeFontFamily = new("Consolas");
    private static readonly Size InfiniteSize = new(double.PositiveInfinity, double.PositiveInfinity);

    private readonly Popup _popup;
    private readonly Border _container;
    private readonly Image _imageControl;
    private readonly TextBlock _timeTextBlock;

    private Window? _hookedWindow;
    private UIElement? _hookedTarget;
    private long _lastFormattedSeconds = -1;
    private bool _lastUseHours;
    private bool _lastHasImage;

    public ThumbnailHoverPopup()
    {
        _imageControl = new Image
        {
            Width = 160,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 0, 5),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Focusable = false
        };
        RenderOptions.SetBitmapScalingMode(_imageControl, BitmapScalingMode.HighQuality);

        _timeTextBlock = new TextBlock
        {
            FontFamily = TimeFontFamily,
            FontSize = 11,
            Foreground = TimeTextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            Text = "00:00",
            IsHitTestVisible = false,
            Focusable = false
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            IsHitTestVisible = false,
            Focusable = false
        };
        stack.Children.Add(_imageControl);
        stack.Children.Add(_timeTextBlock);

        _container = new Border
        {
            Background = PopupBackgroundBrush,
            BorderBrush = PopupBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6),
            SnapsToDevicePixels = true,
            IsHitTestVisible = false,
            Focusable = false,
            Child = stack
        };

        // StaysOpen = true evite la capture souris automatique de WPF (cause de tremblement/blocage de clics).
        _popup = new Popup
        {
            AllowsTransparency = true,
            IsHitTestVisible = false,
            Focusable = false,
            StaysOpen = true,
            Placement = PlacementMode.Relative,
            Child = _container
        };

        _popup.Opened += OnPopupOpened;
        _popup.Closed += OnPopupClosed;
    }

    public void Update(UIElement target, Point mousePosition, TimeSpan time, ImageSource? image)
    {
        Update(target, mousePosition, time, image, null);
    }

    public void Update(UIElement target, Point mousePosition, TimeSpan time, ImageSource? image, TimeSpan? totalDuration)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        bool hasImage = image is not null;
        if (!ReferenceEquals(_imageControl.Source, image))
        {
            _imageControl.Source = image;
        }

        bool visibilityChanged = hasImage != _lastHasImage;
        if (visibilityChanged)
        {
            _imageControl.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
            _lastHasImage = hasImage;
        }

        bool useHours = (totalDuration ?? time).TotalHours >= 1.0;
        long totalSeconds = (long)Math.Floor(time.TotalSeconds);
        if (totalSeconds != _lastFormattedSeconds || useHours != _lastUseHours)
        {
            _lastFormattedSeconds = totalSeconds;
            _lastUseHours = useHours;
            _timeTextBlock.Text = FormatTime(time, useHours);
        }

        if (!ReferenceEquals(_popup.PlacementTarget, target))
        {
            _popup.PlacementTarget = target;
        }

        if (_container.ActualWidth <= 0 || _container.ActualHeight <= 0 || visibilityChanged)
        {
            _container.Measure(InfiniteSize);
        }

        double popupWidth = _container.ActualWidth > 0 ? _container.ActualWidth : _container.DesiredSize.Width;
        double popupHeight = _container.ActualHeight > 0 ? _container.ActualHeight : _container.DesiredSize.Height;

        double offsetX = mousePosition.X - (popupWidth / 2.0);
        double offsetY = mousePosition.Y - popupHeight - VerticalCursorGap;

        ClampOffsetsToWindowAndScreen(target, mousePosition, popupWidth, popupHeight, ref offsetX, ref offsetY);

        _popup.HorizontalOffset = offsetX;
        _popup.VerticalOffset = offsetY;

        if (!_popup.IsOpen)
        {
            AttachDismissHooks(target);
            _popup.IsOpen = true;
        }
    }

    public void Hide()
    {
        if (_popup.IsOpen)
        {
            _popup.IsOpen = false;
        }

        DetachDismissHooks();
    }

    private void OnPopupOpened(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(_container) is HwndSource { Handle: var hwnd } && hwnd != IntPtr.Zero)
        {
            nint exStyle = GetWindowLongPtr(hwnd, GwlExStyle);
            nint updatedStyle = (nint)((long)exStyle | WsExTransparent | WsExNoActivate);
            if (updatedStyle != exStyle)
            {
                SetWindowLongPtr(hwnd, GwlExStyle, updatedStyle);
            }
        }
    }

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        DetachDismissHooks();
    }

    private void AttachDismissHooks(UIElement target)
    {
        if (!ReferenceEquals(_hookedTarget, target))
        {
            DetachDismissHooks();
            _hookedTarget = target;
            _hookedTarget.IsVisibleChanged += OnTargetVisibilityChanged;

            _hookedWindow = Window.GetWindow(target);
            if (_hookedWindow is not null)
            {
                _hookedWindow.Deactivated += OnOwnerWindowDismissSignal;
                _hookedWindow.LocationChanged += OnOwnerWindowDismissSignal;
                _hookedWindow.SizeChanged += OnOwnerWindowSizeChanged;
            }
        }
    }

    private void DetachDismissHooks()
    {
        if (_hookedTarget is not null)
        {
            _hookedTarget.IsVisibleChanged -= OnTargetVisibilityChanged;
            _hookedTarget = null;
        }

        if (_hookedWindow is not null)
        {
            _hookedWindow.Deactivated -= OnOwnerWindowDismissSignal;
            _hookedWindow.LocationChanged -= OnOwnerWindowDismissSignal;
            _hookedWindow.SizeChanged -= OnOwnerWindowSizeChanged;
            _hookedWindow = null;
        }
    }

    private void OnTargetVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            Hide();
        }
    }

    private void OnOwnerWindowDismissSignal(object? sender, EventArgs e)
    {
        Hide();
    }

    private void OnOwnerWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Hide();
    }

    private static void ClampOffsetsToWindowAndScreen(
        UIElement target,
        Point mousePosition,
        double popupWidth,
        double popupHeight,
        ref double offsetX,
        ref double offsetY)
    {
        Window? window = Window.GetWindow(target);
        if (window is not null && PresentationSource.FromVisual(target) is not null)
        {
            Point originInWindow = target.TranslatePoint(new Point(0, 0), window);
            double minWindowX = -originInWindow.X + HorizontalPaddingMargin;
            double maxWindowX = window.ActualWidth - originInWindow.X - popupWidth - HorizontalPaddingMargin;
            if (maxWindowX >= minWindowX)
            {
                offsetX = Math.Clamp(offsetX, minWindowX, maxWindowX);
            }
        }

        PresentationSource? source = PresentationSource.FromVisual(target);
        if (source?.CompositionTarget is not null)
        {
            Point screenOriginPx = target.PointToScreen(new Point(0, 0));
            Point screenOriginDip = source.CompositionTarget.TransformFromDevice.Transform(screenOriginPx);

            Rect workArea = SystemParameters.WorkArea;
            double minScreenX = workArea.Left - screenOriginDip.X + HorizontalPaddingMargin;
            double maxScreenX = workArea.Right - screenOriginDip.X - popupWidth - HorizontalPaddingMargin;
            if (maxScreenX >= minScreenX)
            {
                offsetX = Math.Clamp(offsetX, minScreenX, maxScreenX);
            }

            double minScreenY = workArea.Top - screenOriginDip.Y + HorizontalPaddingMargin;
            if (offsetY < minScreenY)
            {
                double belowY = mousePosition.Y + BelowCursorGap;
                double maxScreenY = workArea.Bottom - screenOriginDip.Y - popupHeight - HorizontalPaddingMargin;
                offsetY = belowY <= maxScreenY ? belowY : minScreenY;
            }
        }
    }

    private static string FormatTime(TimeSpan time, bool includeHours)
    {
        int totalHours = (int)time.TotalHours;
        return includeHours
            ? $"{totalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2}"
            : $"{(int)time.TotalMinutes:D2}:{time.Seconds:D2}";
    }

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
