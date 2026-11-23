using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;

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

    private static readonly IBrush PopupBackgroundBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x1A, 0x1D, 0x23));
    private static readonly IBrush PopupBorderBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x2E, 0x34, 0x40));
    private static readonly IBrush TimeTextBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xED, 0xF0, 0xF5));
    private static readonly FontFamily TimeFontFamily = new("Consolas");

    private readonly Popup _popup;
    private readonly Border _container;
    private readonly Image _imageControl;
    private readonly TextBlock _timeTextBlock;

    private Window? _hookedWindow;
    private Control? _hookedTarget;
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
            IsVisible = false,
            IsHitTestVisible = false,
            Focusable = false
        };
        RenderOptions.SetBitmapInterpolationMode(_imageControl, BitmapInterpolationMode.HighQuality);

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
            IsHitTestVisible = false,
            Focusable = false,
            Child = stack
        };

        // ShouldUseOverlayLayer=false permet de flotter au-dessus de la surface native LibVLC,
        // IsLightDismissEnabled=false evite toute capture de souris ou blocage de clic.
        _popup = new Popup
        {
            IsHitTestVisible = false,
            Focusable = false,
            IsLightDismissEnabled = false,
            ShouldUseOverlayLayer = false,
            Topmost = true,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = PopupAnchor.TopLeft,
            PlacementGravity = PopupGravity.BottomRight,
            PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.None,
            Child = _container
        };

        _popup.Opened += OnPopupOpened;
        _popup.Closed += OnPopupClosed;
    }

    public void Update(Control target, Point mousePosition, TimeSpan time, IImage? image)
    {
        Update(target, mousePosition, time, image, null);
    }

    public void Update(Control target, Point mousePosition, TimeSpan time, IImage? image, TimeSpan? totalDuration)
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
            _imageControl.IsVisible = hasImage;
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

        if (_container.Bounds.Width <= 0 || _container.Bounds.Height <= 0 || visibilityChanged)
        {
            _container.Measure(Size.Infinity);
        }

        double popupWidth = _container.Bounds.Width > 0 ? _container.Bounds.Width : _container.DesiredSize.Width;
        double popupHeight = _container.Bounds.Height > 0 ? _container.Bounds.Height : _container.DesiredSize.Height;

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
        if (TopLevel.GetTopLevel(_container)?.TryGetPlatformHandle()?.Handle is nint hwnd && hwnd != IntPtr.Zero)
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

    private void AttachDismissHooks(Control target)
    {
        if (!ReferenceEquals(_hookedTarget, target))
        {
            DetachDismissHooks();
            _hookedTarget = target;
            _hookedTarget.PropertyChanged += OnTargetPropertyChanged;

            if (TopLevel.GetTopLevel(target) is Window window)
            {
                _hookedWindow = window;
                _hookedWindow.Deactivated += OnOwnerWindowDismissSignal;
                _hookedWindow.PositionChanged += OnOwnerWindowPositionChanged;
                _hookedWindow.SizeChanged += OnOwnerWindowSizeChanged;
            }
        }
    }

    private void DetachDismissHooks()
    {
        if (_hookedTarget is not null)
        {
            _hookedTarget.PropertyChanged -= OnTargetPropertyChanged;
            _hookedTarget = null;
        }

        if (_hookedWindow is not null)
        {
            _hookedWindow.Deactivated -= OnOwnerWindowDismissSignal;
            _hookedWindow.PositionChanged -= OnOwnerWindowPositionChanged;
            _hookedWindow.SizeChanged -= OnOwnerWindowSizeChanged;
            _hookedWindow = null;
        }
    }

    private void OnTargetPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty && e.NewValue is false)
        {
            Hide();
        }
    }

    private void OnOwnerWindowDismissSignal(object? sender, EventArgs e)
    {
        Hide();
    }

    private void OnOwnerWindowPositionChanged(object? sender, PixelPointEventArgs e)
    {
        Hide();
    }

    private void OnOwnerWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        Hide();
    }

    private static void ClampOffsetsToWindowAndScreen(
        Control target,
        Point mousePosition,
        double popupWidth,
        double popupHeight,
        ref double offsetX,
        ref double offsetY)
    {
        TopLevel? topLevel = TopLevel.GetTopLevel(target);
        if (topLevel is null)
        {
            return;
        }

        if (target.TranslatePoint(new Point(0, 0), topLevel) is Point originInTopLevel)
        {
            double minWindowX = -originInTopLevel.X + HorizontalPaddingMargin;
            double maxWindowX = topLevel.ClientSize.Width - originInTopLevel.X - popupWidth - HorizontalPaddingMargin;
            if (maxWindowX >= minWindowX)
            {
                offsetX = Math.Clamp(offsetX, minWindowX, maxWindowX);
            }
        }

        if (topLevel is Window window && window.Screens.ScreenFromVisual(target) is Screen screen)
        {
            double scaling = topLevel.RenderScaling > 0 ? topLevel.RenderScaling : screen.Scaling;
            if (scaling <= 0)
            {
                scaling = 1.0;
            }

            PixelPoint screenOriginPx = target.PointToScreen(new Point(0, 0));
            PixelRect workArea = screen.WorkingArea;

            double minScreenX = ((workArea.X - screenOriginPx.X) / scaling) + HorizontalPaddingMargin;
            double maxScreenX = ((workArea.Right - screenOriginPx.X) / scaling) - popupWidth - HorizontalPaddingMargin;
            if (maxScreenX >= minScreenX)
            {
                offsetX = Math.Clamp(offsetX, minScreenX, maxScreenX);
            }

            double minScreenY = ((workArea.Y - screenOriginPx.Y) / scaling) + HorizontalPaddingMargin;
            if (offsetY < minScreenY)
            {
                double belowY = mousePosition.Y + BelowCursorGap;
                double maxScreenY = ((workArea.Bottom - screenOriginPx.Y) / scaling) - popupHeight - HorizontalPaddingMargin;
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
}
