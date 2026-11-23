using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VideoSplitter.Services;

namespace VideoSplitter.Controls;

public partial class SeekBar : UserControl
{
    private const double HorizontalMargin = 8.0;
    private const double TrackHeight = 5.0;
    private const double TrackCornerRadius = 2.5;
    private const double ThumbRadiusNormal = 6.5;
    private const double ThumbRadiusActive = 7.5;

    private static readonly SolidColorBrush TrackBackgroundBrush = CreateBrush(0x22, 0x26, 0x2E);
    private static readonly SolidColorBrush TrackDisabledBrush = CreateBrush(0x18, 0x1B, 0x20);
    private static readonly SolidColorBrush PlayedBrush = CreateBrush(0x3B, 0x82, 0xF6);
    private static readonly SolidColorBrush ThumbFillBrush = CreateBrush(0xED, 0xF0, 0xF5);
    private static readonly SolidColorBrush ThumbDisabledBrush = CreateBrush(0x55, 0x5C, 0x69);
    private static readonly SolidColorBrush HoverTickBrush = CreateBrush(0x80, 0xED, 0xF0, 0xF5);

    private static readonly Pen TrackBorderPen = CreatePen(0x2E, 0x34, 0x40, 1.0);
    private static readonly Pen ThumbStrokePen = CreatePen(0x3B, 0x82, 0xF6, 2.0);
    private static readonly Pen ThumbDisabledStrokePen = CreatePen(0x2E, 0x34, 0x40, 1.5);

    private readonly ThumbnailHoverPopup _hoverPopup = new();

    private bool _isDragging;
    private bool _isHovering;
    private double _hoverX;
    private TimeSpan _dragPosition;

    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(
        nameof(Duration),
        typeof(TimeSpan),
        typeof(SeekBar),
        new FrameworkPropertyMetadata(TimeSpan.Zero, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position),
        typeof(TimeSpan),
        typeof(SeekBar),
        new FrameworkPropertyMetadata(TimeSpan.Zero, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThumbnailsProperty = DependencyProperty.Register(
        nameof(Thumbnails),
        typeof(IThumbnailProvider),
        typeof(SeekBar),
        new PropertyMetadata(null));

    public TimeSpan Duration
    {
        get => (TimeSpan)GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public TimeSpan Position
    {
        get => (TimeSpan)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public IThumbnailProvider? Thumbnails
    {
        get => (IThumbnailProvider?)GetValue(ThumbnailsProperty);
        set => SetValue(ThumbnailsProperty, value);
    }

    public event EventHandler<TimeSpan>? SeekRequested;
    public event EventHandler<TimeSpan>? SeekCompleted;

    public SeekBar()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _hoverPopup.Hide();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= HorizontalMargin * 2 || height <= 0)
        {
            return;
        }

        double trackWidth = width - (HorizontalMargin * 2);
        double trackTop = (height - TrackHeight) / 2.0;
        var trackRect = new Rect(HorizontalMargin, trackTop, trackWidth, TrackHeight);

        bool enabled = IsEnabled && Duration > TimeSpan.Zero;
        dc.DrawRoundedRectangle(
            enabled ? TrackBackgroundBrush : TrackDisabledBrush,
            TrackBorderPen,
            trackRect,
            TrackCornerRadius,
            TrackCornerRadius);

        TimeSpan effectivePosition = _isDragging ? _dragPosition : Position;
        double ratio = Duration > TimeSpan.Zero
            ? Math.Clamp(effectivePosition.TotalSeconds / Duration.TotalSeconds, 0.0, 1.0)
            : 0.0;
        double thumbX = HorizontalMargin + (ratio * trackWidth);

        if (enabled && thumbX > HorizontalMargin)
        {
            var playedRect = new Rect(HorizontalMargin, trackTop, thumbX - HorizontalMargin, TrackHeight);
            dc.DrawRoundedRectangle(PlayedBrush, null, playedRect, TrackCornerRadius, TrackCornerRadius);
        }

        if (enabled && _isHovering)
        {
            double clampedHoverX = Math.Clamp(_hoverX, HorizontalMargin, HorizontalMargin + trackWidth);
            dc.DrawRectangle(HoverTickBrush, null, new Rect(clampedHoverX - 1.0, trackTop - 3.0, 2.0, TrackHeight + 6.0));
        }

        double radius = (_isDragging || _isHovering) && enabled ? ThumbRadiusActive : ThumbRadiusNormal;
        dc.DrawEllipse(
            enabled ? ThumbFillBrush : ThumbDisabledBrush,
            enabled ? ThumbStrokePen : ThumbDisabledStrokePen,
            new Point(thumbX, height / 2.0),
            radius,
            radius);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (!IsEnabled || Duration <= TimeSpan.Zero)
        {
            return;
        }

        Point pos = e.GetPosition(this);
        TimeSpan target = XToTime(pos.X);

        _isDragging = true;
        _isHovering = true;
        _hoverX = pos.X;
        _dragPosition = target;

        CaptureMouse();
        InvalidateVisual();
        UpdatePopup(pos, target);

        SeekRequested?.Invoke(this, target);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!IsEnabled || Duration <= TimeSpan.Zero)
        {
            _hoverPopup.Hide();
            return;
        }

        Point pos = e.GetPosition(this);
        TimeSpan hoverTime = XToTime(pos.X);

        _isHovering = true;
        _hoverX = pos.X;

        if (_isDragging)
        {
            _dragPosition = hoverTime;
            SeekRequested?.Invoke(this, hoverTime);
        }

        UpdatePopup(pos, hoverTime);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (!_isDragging)
        {
            return;
        }

        Point pos = e.GetPosition(this);
        TimeSpan finalTime = XToTime(pos.X);

        _isDragging = false;
        _dragPosition = finalTime;
        ReleaseMouseCapture();

        SeekCompleted?.Invoke(this, finalTime);

        if (!IsMouseOver)
        {
            _isHovering = false;
            _hoverPopup.Hide();
        }
        else
        {
            UpdatePopup(pos, finalTime);
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);

        if (!_isDragging)
        {
            _isHovering = false;
            _hoverPopup.Hide();
            InvalidateVisual();
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        if (_isDragging)
        {
            _isDragging = false;
            if (!IsMouseOver)
            {
                _isHovering = false;
                _hoverPopup.Hide();
            }

            InvalidateVisual();
        }
    }

    private TimeSpan XToTime(double x)
    {
        double trackWidth = ActualWidth - (HorizontalMargin * 2);
        if (trackWidth <= 0 || Duration <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        double ratio = Math.Clamp((x - HorizontalMargin) / trackWidth, 0.0, 1.0);
        return TimeSpan.FromSeconds(ratio * Duration.TotalSeconds);
    }

    private void UpdatePopup(Point mousePosition, TimeSpan time)
    {
        ImageSource? image = Thumbnails?.GetThumbnail(time);
        _hoverPopup.Update(this, mousePosition, time, image, Duration);
    }

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush CreateBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(byte r, byte g, byte b, double thickness)
    {
        var pen = new Pen(CreateBrush(r, g, b), thickness);
        pen.Freeze();
        return pen;
    }
}
