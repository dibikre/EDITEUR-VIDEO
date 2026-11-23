using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace VideoSplitter.Controls;

public sealed class ThumbnailHoverPopup
{
    private const double VerticalCursorGap = 8.0;
    private const double HorizontalPaddingMargin = 4.0;

    private static readonly SolidColorBrush PopupBackgroundBrush = CreateFrozenBrush(0x1A, 0x1D, 0x23);
    private static readonly SolidColorBrush PopupBorderBrush = CreateFrozenBrush(0x2E, 0x34, 0x40);
    private static readonly SolidColorBrush TimeTextBrush = CreateFrozenBrush(0xED, 0xF0, 0xF5);
    private static readonly FontFamily TimeFontFamily = new("Consolas");
    private static readonly Size InfiniteSize = new(double.PositiveInfinity, double.PositiveInfinity);

    private readonly Popup _popup;
    private readonly Border _container;
    private readonly Image _imageControl;
    private readonly TextBlock _timeTextBlock;

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
            Visibility = Visibility.Collapsed
        };
        RenderOptions.SetBitmapScalingMode(_imageControl, BitmapScalingMode.HighQuality);

        _timeTextBlock = new TextBlock
        {
            FontFamily = TimeFontFamily,
            FontSize = 11,
            Foreground = TimeTextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            Text = "00:00"
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical
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
            Child = stack
        };

        _popup = new Popup
        {
            AllowsTransparency = true,
            IsHitTestVisible = false,
            StaysOpen = false,
            Placement = PlacementMode.Relative,
            Child = _container
        };
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

        if (_popup.PlacementTarget != target)
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

        ClampOffsetsToWindowAndScreen(target, popupWidth, popupHeight, ref offsetX, ref offsetY);

        _popup.HorizontalOffset = offsetX;
        _popup.VerticalOffset = offsetY;

        if (!_popup.IsOpen)
        {
            _popup.IsOpen = true;
        }
    }

    public void Hide()
    {
        if (_popup.IsOpen)
        {
            _popup.IsOpen = false;
        }
    }

    private static void ClampOffsetsToWindowAndScreen(
        UIElement target,
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
                offsetY = minScreenY;
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
