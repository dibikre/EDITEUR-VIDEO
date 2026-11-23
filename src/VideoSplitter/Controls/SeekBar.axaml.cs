using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using VideoSplitter.Services;

namespace VideoSplitter.Controls;

public partial class SeekBar : UserControl
{
    private const double HorizontalMargin = 8.0;
    private const double TrackHeight = 5.0;
    private const double TrackCornerRadius = 2.5;
    private const double ThumbRadiusNormal = 6.5;
    private const double ThumbRadiusActive = 7.5;

    private static readonly IBrush TrackBackgroundBrush = CreateBrush(0x22, 0x26, 0x2E);
    private static readonly IBrush TrackDisabledBrush = CreateBrush(0x18, 0x1B, 0x20);
    private static readonly IBrush PlayedBrush = CreateBrush(0x3B, 0x82, 0xF6);
    private static readonly IBrush ThumbFillBrush = CreateBrush(0xED, 0xF0, 0xF5);
    private static readonly IBrush ThumbDisabledBrush = CreateBrush(0x55, 0x5C, 0x69);
    private static readonly IBrush HoverTickBrush = CreateBrush(0x80, 0xED, 0xF0, 0xF5);

    private static readonly IPen TrackBorderPen = CreatePen(0x2E, 0x34, 0x40, 1.0);
    private static readonly IPen ThumbStrokePen = CreatePen(0x3B, 0x82, 0xF6, 2.0);
    private static readonly IPen ThumbDisabledStrokePen = CreatePen(0x2E, 0x34, 0x40, 1.5);

    private readonly ThumbnailHoverPopup _hoverPopup = new();

    private bool _isDragging;
    private bool _isHovering;
    private double _hoverX;
    private TimeSpan _dragPosition;

    public static readonly StyledProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.Register<SeekBar, TimeSpan>(nameof(Duration), TimeSpan.Zero);

    public static readonly StyledProperty<TimeSpan> PositionProperty =
        AvaloniaProperty.Register<SeekBar, TimeSpan>(nameof(Position), TimeSpan.Zero);

    public static readonly StyledProperty<IThumbnailProvider?> ThumbnailsProperty =
        AvaloniaProperty.Register<SeekBar, IThumbnailProvider?>(nameof(Thumbnails), null);

    static SeekBar()
    {
        AffectsRender<SeekBar>(DurationProperty, PositionProperty, IsEnabledProperty);
    }

    public TimeSpan Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public TimeSpan Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public IThumbnailProvider? Thumbnails
    {
        get => GetValue(ThumbnailsProperty);
        set => SetValue(ThumbnailsProperty, value);
    }

    public event EventHandler<TimeSpan>? SeekRequested;
    public event EventHandler<TimeSpan>? SeekCompleted;

    public SeekBar()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        _hoverPopup.Hide();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= HorizontalMargin * 2 || height <= 0)
        {
            return;
        }

        double trackWidth = width - (HorizontalMargin * 2);
        double trackTop = (height - TrackHeight) / 2.0;
        var trackRect = new Rect(HorizontalMargin, trackTop, trackWidth, TrackHeight);

        bool enabled = IsEnabled && Duration > TimeSpan.Zero;
        context.DrawRectangle(
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
            context.DrawRectangle(PlayedBrush, null, playedRect, TrackCornerRadius, TrackCornerRadius);
        }

        if (enabled && _isHovering)
        {
            double clampedHoverX = Math.Clamp(_hoverX, HorizontalMargin, HorizontalMargin + trackWidth);
            context.DrawRectangle(HoverTickBrush, null, new Rect(clampedHoverX - 1.0, trackTop - 3.0, 2.0, TrackHeight + 6.0));
        }

        double radius = (_isDragging || _isHovering) && enabled ? ThumbRadiusActive : ThumbRadiusNormal;
        context.DrawEllipse(
            enabled ? ThumbFillBrush : ThumbDisabledBrush,
            enabled ? ThumbStrokePen : ThumbDisabledStrokePen,
            new Point(thumbX, height / 2.0),
            radius,
            radius);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!IsEnabled || Duration <= TimeSpan.Zero)
        {
            return;
        }

        PointerPoint pointerPoint = e.GetCurrentPoint(this);
        if (!pointerPoint.Properties.IsLeftButtonPressed)
        {
            return;
        }

        Point pos = pointerPoint.Position;
        TimeSpan target = XToTime(pos.X);

        _isDragging = true;
        _isHovering = true;
        _hoverX = pos.X;
        _dragPosition = target;

        e.Pointer.Capture(this);
        InvalidateVisual();
        UpdatePopup(pos, target);

        SeekRequested?.Invoke(this, target);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

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

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (!_isDragging || e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        Point pos = e.GetPosition(this);
        TimeSpan finalTime = XToTime(pos.X);

        _isDragging = false;
        _dragPosition = finalTime;
        if (Equals(e.Pointer.Captured, this))
        {
            e.Pointer.Capture(null);
        }

        SeekCompleted?.Invoke(this, finalTime);

        if (!IsPointerOver)
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

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        if (!_isDragging)
        {
            _isHovering = false;
            _hoverPopup.Hide();
            InvalidateVisual();
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);

        if (_isDragging)
        {
            _isDragging = false;
            if (!IsPointerOver)
            {
                _isHovering = false;
                _hoverPopup.Hide();
            }

            InvalidateVisual();
        }
    }

    private TimeSpan XToTime(double x)
    {
        double trackWidth = Bounds.Width - (HorizontalMargin * 2);
        if (trackWidth <= 0 || Duration <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        double ratio = Math.Clamp((x - HorizontalMargin) / trackWidth, 0.0, 1.0);
        return TimeSpan.FromSeconds(ratio * Duration.TotalSeconds);
    }

    private void UpdatePopup(Point mousePosition, TimeSpan time)
    {
        IImage? image = Thumbnails?.GetThumbnail(time);
        _hoverPopup.Update(this, mousePosition, time, image, Duration);
    }

    private static IImmutableBrush CreateBrush(byte r, byte g, byte b)
        => new ImmutableSolidColorBrush(Color.FromRgb(r, g, b));

    private static IImmutableBrush CreateBrush(byte a, byte r, byte g, byte b)
        => new ImmutableSolidColorBrush(Color.FromArgb(a, r, g, b));

    private static IPen CreatePen(byte r, byte g, byte b, double thickness)
        => new ImmutablePen(CreateBrush(r, g, b), thickness);
}
