using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using VideoSplitter.Editing;
using VideoSplitter.Models;
using VideoSplitter.Services;

namespace VideoSplitter.Controls;

public sealed class TimelineControl : Control
{
    public const double TotalTimelineHeight = 135.0;
    private const double RulerHeight = 24.0;
    private const double FilmstripTop = 26.0;
    private const double FilmstripHeight = 80.0;
    private const double SegmentsBandTop = 108.0;
    private const double SegmentsBandHeight = 22.0;
    private const double MaxPixelsPerSecond = 2000.0;
    private const double WheelZoomFactor = 1.25;
    private const double EdgeScrollMarginPx = 20.0;
    private const double MinMajorTickSpacingPx = 90.0;
    private const double MinMinorTickSpacingPx = 8.0;
    private const double MarkerHitHalfWidthPx = 6.0;
    private const double MarkerDragThresholdPx = 3.0;
    private const double PlayheadDragThresholdPx = 4.0;
    private const double MaxSnapToleranceSeconds = 0.5;
    private const double SnapTolerancePx = 8.0;
    private const long FollowSuspendDurationMs = 3000;
    private const int MaxFormattedTextCacheEntries = 256;

    private readonly record struct SegmentLabelKey(string Name, int WidthBucket, bool Enabled);

    private static readonly double[] MajorStepCandidates =
    [
        0.1, 0.2, 0.5, 1.0, 2.0, 5.0, 10.0, 15.0, 30.0,
        60.0, 120.0, 300.0, 600.0, 900.0, 1800.0, 3600.0, 7200.0
    ];

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor ResizeHorizontalCursor = new(StandardCursorType.SizeWestEast);

    private readonly ThumbnailHoverPopup _hoverPopup = new();
    private readonly DispatcherTimer _edgeScrollTimer;
    private readonly Dictionary<string, FormattedText> _labelCache = new(MaxFormattedTextCacheEntries);
    private readonly Dictionary<SegmentLabelKey, FormattedText> _segmentLabelCache = new(MaxFormattedTextCacheEntries);

    // Ressources graphiques immuables
    private IImmutableBrush _backgroundBrush = Brushes.Transparent;
    private IImmutableBrush _rulerBackgroundBrush = Brushes.Transparent;
    private IImmutableBrush _tilePlaceholderBrush = Brushes.Transparent;
    private IImmutableBrush _textPrimaryBrush = Brushes.White;
    private IImmutableBrush _textSecondaryBrush = Brushes.Gray;
    private IImmutableBrush _textMutedBrush = Brushes.Gray;
    private IImmutableBrush _playheadBrush = Brushes.DodgerBlue;
    private IImmutableBrush _segmentEvenBrush = Brushes.DarkSlateBlue;
    private IImmutableBrush _segmentOddBrush = Brushes.SlateBlue;
    private IImmutableBrush _segmentDisabledBrush = Brushes.DimGray;
    private IImmutableBrush _disabledFilmstripScrimBrush = Brushes.Transparent;
    private IImmutableBrush _markerBrush = Brushes.Orange;
    private IImmutableBrush _markerSelectedBrush = Brushes.Red;
    private IImmutableBrush _pendingStartBrush = Brushes.LimeGreen;
    private IPen _borderPen = null!;
    private IPen _majorTickPen = null!;
    private IPen _minorTickPen = null!;
    private IPen _playheadPen = null!;
    private IPen _hoverPen = null!;
    private IPen _segmentBorderPen = null!;
    private IPen _segmentSelectedPen = null!;
    private IPen _markerPen = null!;
    private IPen _markerSelectedPen = null!;
    private IPen _markerHandleOutlinePen = null!;
    private IPen _pendingStartPen = null!;
    private IImmutableBrush _zoomRangeFillBrush = Brushes.Transparent;
    private IImmutableBrush _zoomRangeBadgeBrush = Brushes.Transparent;
    private IPen _zoomRangeBorderPen = null!;
    private StreamGeometry _markerDiamondGeometry = null!;
    private Typeface _labelTypeface;
    private Typeface _emptyTypeface;
    private bool _resourcesInitialized;

    // Etat de rendu regroupe
    private bool _renderScheduled;
    private bool _isAttached;

    // Etat d'extrapolation du playhead
    private bool _isRenderingSubscribed;
    private double _anchorPositionSeconds;
    private long _anchorTimestampTicks;

    // Etat d'interaction souris / suivi
    private IPointer? _capturedPointer;
    private bool _isDragging;
    private double _playheadDragStartX;
    private bool _hasPlayheadDragMoved;
    private bool _isDraggingMarker;
    private bool _hasMarkerDragMoved;
    private int _dragMarkerIndex = -1;
    private double _dragMarkerStartX;
    private double _dragMarkerOriginalSeconds;
    private double _dragMarkerSeconds;
    private bool _isSelectingZoomRange;
    private double _zoomSelectStartX;
    private double _zoomSelectStartSeconds;
    private double _zoomSelectCurrentSeconds;
    private bool _isHovering;
    private double _hoverX;
    private double _lastDragX;
    private double _dragPositionSeconds;
    private KeyModifiers _lastKeyModifiers;
    private long _followSuspendedUntilTick;
    private bool _isUpdatingViewportProps;

    public static readonly StyledProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.Register<TimelineControl, TimeSpan>(nameof(Duration), TimeSpan.Zero);

    public static readonly StyledProperty<TimeSpan> PositionProperty =
        AvaloniaProperty.Register<TimelineControl, TimeSpan>(nameof(Position), TimeSpan.Zero);

    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<TimelineControl, bool>(nameof(IsPlaying), false);

    public static readonly StyledProperty<double> PlaybackSpeedProperty =
        AvaloniaProperty.Register<TimelineControl, double>(nameof(PlaybackSpeed), 1.0);

    public static readonly StyledProperty<IThumbnailProvider?> ThumbnailsProperty =
        AvaloniaProperty.Register<TimelineControl, IThumbnailProvider?>(nameof(Thumbnails), null);

    public static readonly StyledProperty<IRangeThumbnailProvider?> RangeThumbnailsProperty =
        AvaloniaProperty.Register<TimelineControl, IRangeThumbnailProvider?>(nameof(RangeThumbnails), null);

    public static readonly StyledProperty<double> PixelsPerSecondProperty =
        AvaloniaProperty.Register<TimelineControl, double>(
            nameof(PixelsPerSecond),
            10.0,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> ScrollOffsetSecondsProperty =
        AvaloniaProperty.Register<TimelineControl, double>(
            nameof(ScrollOffsetSeconds),
            0.0,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsFitToWindowProperty =
        AvaloniaProperty.Register<TimelineControl, bool>(
            nameof(IsFitToWindow),
            true,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsRangeZoomActiveProperty =
        AvaloniaProperty.Register<TimelineControl, bool>(
            nameof(IsRangeZoomActive),
            false,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> FollowPlaybackProperty =
        AvaloniaProperty.Register<TimelineControl, bool>(nameof(FollowPlayback), true);

    public static readonly StyledProperty<IReadOnlyList<Segment>?> SegmentsProperty =
        AvaloniaProperty.Register<TimelineControl, IReadOnlyList<Segment>?>(nameof(Segments), null);

    public static readonly StyledProperty<Guid?> SelectedSegmentIdProperty =
        AvaloniaProperty.Register<TimelineControl, Guid?>(nameof(SelectedSegmentId), null);

    public static readonly StyledProperty<int?> SelectedMarkerIndexProperty =
        AvaloniaProperty.Register<TimelineControl, int?>(nameof(SelectedMarkerIndex), null);

    public static readonly StyledProperty<IReadOnlyList<double>?> KeyframesProperty =
        AvaloniaProperty.Register<TimelineControl, IReadOnlyList<double>?>(nameof(Keyframes), null);

    public static readonly StyledProperty<bool> SnapToKeyframesProperty =
        AvaloniaProperty.Register<TimelineControl, bool>(nameof(SnapToKeyframes), true);

    public static readonly StyledProperty<TimeSpan?> PendingSegmentStartProperty =
        AvaloniaProperty.Register<TimelineControl, TimeSpan?>(nameof(PendingSegmentStart), null);

    static TimelineControl()
    {
        DurationProperty.Changed.AddClassHandler<TimelineControl>((c, _) => c.HandleDurationChanged());
        PositionProperty.Changed.AddClassHandler<TimelineControl>((c, _) => c.HandlePositionChanged());
        IsPlayingProperty.Changed.AddClassHandler<TimelineControl>((c, _) => c.HandleIsPlayingChanged());
        PlaybackSpeedProperty.Changed.AddClassHandler<TimelineControl>((c, _) => c.ReanchorPlayhead(c.GetCurrentExtrapolatedSeconds()));
        ThumbnailsProperty.Changed.AddClassHandler<TimelineControl>((c, e) => c.HandleThumbnailsChanged(e));
        RangeThumbnailsProperty.Changed.AddClassHandler<TimelineControl>((c, _) => c.ScheduleRender());
        PixelsPerSecondProperty.Changed.AddClassHandler<TimelineControl>((c, _) => c.HandleViewportPropertyChanged());
        ScrollOffsetSecondsProperty.Changed.AddClassHandler<TimelineControl>((c, _) => c.HandleViewportPropertyChanged());
        IsFitToWindowProperty.Changed.AddClassHandler<TimelineControl>((c, e) => c.HandleIsFitToWindowChanged(e));
        SegmentsProperty.Changed.AddClassHandler<TimelineControl>((c, _) => c.HandleSegmentsChanged());
        SelectedSegmentIdProperty.Changed.AddClassHandler<TimelineControl>((c, _) => c.ScheduleRender());
        SelectedMarkerIndexProperty.Changed.AddClassHandler<TimelineControl>((c, _) => c.ScheduleRender());
        PendingSegmentStartProperty.Changed.AddClassHandler<TimelineControl>((c, _) => c.ScheduleRender());
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

    public bool IsPlaying
    {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public double PlaybackSpeed
    {
        get => GetValue(PlaybackSpeedProperty);
        set => SetValue(PlaybackSpeedProperty, value);
    }

    public IThumbnailProvider? Thumbnails
    {
        get => GetValue(ThumbnailsProperty);
        set => SetValue(ThumbnailsProperty, value);
    }

    public IRangeThumbnailProvider? RangeThumbnails
    {
        get => GetValue(RangeThumbnailsProperty);
        set => SetValue(RangeThumbnailsProperty, value);
    }

    public double PixelsPerSecond
    {
        get => GetValue(PixelsPerSecondProperty);
        set => SetValue(PixelsPerSecondProperty, value);
    }

    public double ScrollOffsetSeconds
    {
        get => GetValue(ScrollOffsetSecondsProperty);
        set => SetValue(ScrollOffsetSecondsProperty, value);
    }

    public bool IsFitToWindow
    {
        get => GetValue(IsFitToWindowProperty);
        set => SetValue(IsFitToWindowProperty, value);
    }

    public bool IsRangeZoomActive
    {
        get => GetValue(IsRangeZoomActiveProperty);
        set => SetValue(IsRangeZoomActiveProperty, value);
    }

    public bool FollowPlayback
    {
        get => GetValue(FollowPlaybackProperty);
        set => SetValue(FollowPlaybackProperty, value);
    }

    public IReadOnlyList<Segment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public Guid? SelectedSegmentId
    {
        get => GetValue(SelectedSegmentIdProperty);
        set => SetValue(SelectedSegmentIdProperty, value);
    }

    public int? SelectedMarkerIndex
    {
        get => GetValue(SelectedMarkerIndexProperty);
        set => SetValue(SelectedMarkerIndexProperty, value);
    }

    public IReadOnlyList<double>? Keyframes
    {
        get => GetValue(KeyframesProperty);
        set => SetValue(KeyframesProperty, value);
    }

    public bool SnapToKeyframes
    {
        get => GetValue(SnapToKeyframesProperty);
        set => SetValue(SnapToKeyframesProperty, value);
    }

    public TimeSpan? PendingSegmentStart
    {
        get => GetValue(PendingSegmentStartProperty);
        set => SetValue(PendingSegmentStartProperty, value);
    }

    public event EventHandler<TimeSpan>? SeekRequested;
    public event EventHandler<TimeSpan>? SeekCompleted;
    public event EventHandler<TimeSpan>? SetInRequested;
    public event EventHandler<TimeSpan>? SetOutRequested;
    public event EventHandler? ViewportChanged;
    public event EventHandler<Guid?>? SegmentSelectionRequested;
    public event EventHandler<int?>? MarkerSelectionRequested;
    public event EventHandler<(int Index, TimeSpan Time)>? BoundaryDragPreview;
    public event EventHandler<(int Index, TimeSpan Time, bool Cancelled)>? BoundaryDragCompleted;
    public event EventHandler<(double StartSeconds, double EndSeconds, double ViewportWidth)>? RangeZoomSelected;

    public TimelineControl()
    {
        Cursor = HandCursor;
        ClipToBounds = true;

        _edgeScrollTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _edgeScrollTimer.Tick += OnEdgeScrollTick;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 0.0 : availableSize.Width;
        return new Size(width, TotalTimelineHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        SyncViewportToSize(finalSize.Width);
        ScheduleRender();
        return finalSize;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        EnsureRenderResources();
        SyncViewportToSize(Bounds.Width);
        UpdateRenderingSubscription();
        ScheduleRender();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isAttached = false;
        _edgeScrollTimer.Stop();
        _hoverPopup.Hide();
        UnsubscribeRendering();
    }

    private void HandleThumbnailsChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is IThumbnailProvider oldProvider)
        {
            oldProvider.ThumbnailsChanged -= OnProviderThumbnailsChanged;
        }

        if (e.NewValue is IThumbnailProvider newProvider)
        {
            newProvider.ThumbnailsChanged += OnProviderThumbnailsChanged;
        }

        ScheduleRender();
    }

    private void HandleViewportPropertyChanged()
    {
        if (_isUpdatingViewportProps)
        {
            return;
        }

        ClampViewportProps();
        ScheduleRender();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandleIsFitToWindowChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (!_isUpdatingViewportProps && e.NewValue is true)
        {
            SyncViewportToSize(Bounds.Width);
            ScheduleRender();
        }
    }

    private void HandleSegmentsChanged()
    {
        if (_isDraggingMarker)
        {
            ResetMarkerDragState();
        }

        ScheduleRender();
    }

    private void OnProviderThumbnailsChanged(object? sender, EventArgs e)
    {
        ScheduleRender();
    }

    private void HandleDurationChanged()
    {
        _followSuspendedUntilTick = 0;
        ReanchorPlayhead(Math.Clamp(Position.TotalSeconds, 0.0, GetTotalSeconds()));
        SyncViewportToSize(Bounds.Width);
        UpdateRenderingSubscription();
        ScheduleRender();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandlePositionChanged()
    {
        double totalSec = GetTotalSeconds();
        double posSec = Math.Clamp(Position.TotalSeconds, 0.0, totalSec);
        ReanchorPlayhead(posSec);

        if (!_isDragging && !_isDraggingMarker)
        {
            ScheduleRender();
        }
    }

    private void HandleIsPlayingChanged()
    {
        double currentSec = GetCurrentExtrapolatedSeconds();
        ReanchorPlayhead(currentSec);
        UpdateRenderingSubscription();
        ScheduleRender();
    }

    private void ReanchorPlayhead(double positionSeconds)
    {
        _anchorPositionSeconds = Math.Clamp(positionSeconds, 0.0, GetTotalSeconds());
        _anchorTimestampTicks = Stopwatch.GetTimestamp();
    }

    private void UpdateRenderingSubscription()
    {
        if (IsPlaying && Duration > TimeSpan.Zero && _isAttached)
        {
            StartAnimationLoop();
        }
        else
        {
            UnsubscribeRendering();
        }
    }

    private void StartAnimationLoop()
    {
        if (_isRenderingSubscribed)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        _isRenderingSubscribed = true;
        topLevel.RequestAnimationFrame(OnAnimationFrame);
    }

    private void UnsubscribeRendering()
    {
        _isRenderingSubscribed = false;
    }

    private void OnAnimationFrame(TimeSpan _)
    {
        if (!_isRenderingSubscribed || !_isAttached || !IsPlaying || Duration <= TimeSpan.Zero)
        {
            _isRenderingSubscribed = false;
            return;
        }

        double currentPos = GetCurrentExtrapolatedSeconds();

        if (FollowPlayback && !IsFollowSuspended() && Bounds.Width > 0 && PixelsPerSecond > 0 && !IsFitToWindow)
        {
            double visibleDuration = Bounds.Width / PixelsPerSecond;
            double offset = ScrollOffsetSeconds;
            if (currentPos > offset + visibleDuration || currentPos < offset)
            {
                double pagedOffset = ClampOffset(currentPos - (0.10 * visibleDuration), PixelsPerSecond, Bounds.Width);
                if (Math.Abs(pagedOffset - offset) > 0.0001)
                {
                    SetViewportValues(PixelsPerSecond, pagedOffset, IsFitToWindow);
                    ViewportChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        InvalidateVisual();
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnAnimationFrame);
    }

    public void NotifyUserNavigated()
    {
        _followSuspendedUntilTick = Environment.TickCount64 + FollowSuspendDurationMs;
    }

    public bool CancelActiveDrag()
    {
        if (_isSelectingZoomRange)
        {
            _edgeScrollTimer.Stop();
            _isSelectingZoomRange = false;
            ReleasePointerCapture();
            _hoverPopup.Hide();

            if (!Equals(Cursor, HandCursor))
            {
                Cursor = HandCursor;
            }

            InvalidateVisual();
            return true;
        }

        if (_isDraggingMarker)
        {
            _edgeScrollTimer.Stop();
            int idx = _dragMarkerIndex;
            TimeSpan original = TimeSpan.FromSeconds(_dragMarkerOriginalSeconds);
            ResetMarkerDragState();
            ReleasePointerCapture();
            _hoverPopup.Hide();

            if (!Equals(Cursor, HandCursor))
            {
                Cursor = HandCursor;
            }

            InvalidateVisual();
            BoundaryDragCompleted?.Invoke(this, (idx, original, true));
            return true;
        }

        if (_isDragging)
        {
            _edgeScrollTimer.Stop();
            _isDragging = false;
            _hasPlayheadDragMoved = false;
            ReleasePointerCapture();
            _hoverPopup.Hide();
            InvalidateVisual();
            return true;
        }

        return false;
    }

    public void ZoomByFactorCentered(double factor)
    {
        double totalSeconds = GetTotalSeconds();
        double width = Bounds.Width;
        if (totalSeconds <= 0 || width <= 0)
        {
            return;
        }

        double currentPps = PixelsPerSecond > 0 ? PixelsPerSecond : GetMinPixelsPerSecond(width, totalSeconds);
        double currentOffset = ScrollOffsetSeconds;
        double visibleDuration = width / currentPps;

        double playheadSec = GetEffectivePlayheadSeconds(totalSeconds);
        bool playheadVisible = playheadSec >= currentOffset && playheadSec <= currentOffset + visibleDuration;
        double anchorTime = playheadVisible ? playheadSec : currentOffset + (visibleDuration / 2.0);
        double anchorX = (anchorTime - currentOffset) * currentPps;

        ApplyZoomAtAnchor(factor, anchorTime, anchorX);
    }

    private void ApplyZoomAtAnchor(double factor, double anchorTime, double anchorX)
    {
        double totalSeconds = GetTotalSeconds();
        double width = Bounds.Width;
        if (totalSeconds <= 0 || width <= 0)
        {
            return;
        }

        double minPps = GetMinPixelsPerSecond(width, totalSeconds);
        double currentPps = PixelsPerSecond > 0 ? PixelsPerSecond : minPps;
        double newPps = Math.Clamp(currentPps * factor, minPps, MaxPixelsPerSecond);
        bool isFit = newPps <= minPps + 0.0001;
        double newOffset = isFit ? 0.0 : ClampOffset(anchorTime - (anchorX / newPps), newPps, width);

        NotifyUserNavigated();
        SetViewportValues(newPps, newOffset, isFit: isFit);
        ScheduleRender();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ZoomToRange(double rawStartSeconds, double rawEndSeconds)
    {
        double totalSeconds = GetTotalSeconds();
        double width = Bounds.Width;
        if (totalSeconds <= 0 || width <= 0)
        {
            return;
        }

        double t1 = Math.Clamp(Math.Min(rawStartSeconds, rawEndSeconds), 0.0, totalSeconds);
        double t2 = Math.Clamp(Math.Max(rawStartSeconds, rawEndSeconds), 0.0, totalSeconds);

        double minSpan = Math.Max(0.2, width / MaxPixelsPerSecond);
        if (t2 - t1 < minSpan)
        {
            double mid = (t1 + t2) / 2.0;
            t1 = Math.Clamp(mid - (minSpan / 2.0), 0.0, Math.Max(0.0, totalSeconds - minSpan));
            t2 = Math.Min(totalSeconds, t1 + minSpan);
        }

        double span = Math.Max(0.05, t2 - t1);
        double minPps = GetMinPixelsPerSecond(width, totalSeconds);
        double newPps = Math.Clamp(width / span, minPps, MaxPixelsPerSecond);
        double newOffset = ClampOffset(t1, newPps, width);

        NotifyUserNavigated();
        SetViewportValues(newPps, newOffset, isFit: false);
        SetCurrentValue(IsRangeZoomActiveProperty, true);
        ScheduleRender();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
        RangeZoomSelected?.Invoke(this, (t1, t2, width));
    }

    private void SyncViewportToSize(double width)
    {
        double totalSeconds = GetTotalSeconds();
        if (totalSeconds <= 0 || width <= 0)
        {
            return;
        }

        double minPps = GetMinPixelsPerSecond(width, totalSeconds);
        if (IsFitToWindow)
        {
            SetViewportValues(minPps, 0.0, isFit: true);
        }
        else
        {
            double clampedPps = Math.Clamp(PixelsPerSecond, minPps, MaxPixelsPerSecond);
            double clampedOffset = ClampOffset(ScrollOffsetSeconds, clampedPps, width);
            SetViewportValues(clampedPps, clampedOffset, isFit: false);
        }

        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClampViewportProps()
    {
        double totalSeconds = GetTotalSeconds();
        double width = Bounds.Width;
        if (totalSeconds <= 0 || width <= 0)
        {
            return;
        }

        double minPps = GetMinPixelsPerSecond(width, totalSeconds);
        double clampedPps = Math.Clamp(PixelsPerSecond, minPps, MaxPixelsPerSecond);
        double clampedOffset = ClampOffset(ScrollOffsetSeconds, clampedPps, width);
        SetViewportValues(clampedPps, clampedOffset, IsFitToWindow);
    }

    private void SetViewportValues(double pps, double offset, bool isFit)
    {
        _isUpdatingViewportProps = true;
        try
        {
            if (Math.Abs(PixelsPerSecond - pps) > 0.0001)
            {
                SetCurrentValue(PixelsPerSecondProperty, pps);
            }

            if (Math.Abs(ScrollOffsetSeconds - offset) > 0.0001)
            {
                SetCurrentValue(ScrollOffsetSecondsProperty, offset);
            }

            if (IsFitToWindow != isFit)
            {
                SetCurrentValue(IsFitToWindowProperty, isFit);
            }
        }
        finally
        {
            _isUpdatingViewportProps = false;
        }
    }

    private double GetMinPixelsPerSecond(double width, double totalSeconds)
    {
        if (width <= 0 || totalSeconds <= 0)
        {
            return 1.0;
        }

        return Math.Min(MaxPixelsPerSecond, width / totalSeconds);
    }

    private double ClampOffset(double offset, double pps, double width)
    {
        double totalSeconds = GetTotalSeconds();
        if (totalSeconds <= 0 || width <= 0 || pps <= 0)
        {
            return 0.0;
        }

        double maxOffset = Math.Max(0.0, totalSeconds - (width / pps));
        return Math.Clamp(offset, 0.0, maxOffset);
    }

    private double GetTotalSeconds() => Duration > TimeSpan.Zero ? Duration.TotalSeconds : 0.0;

    private bool IsFollowSuspended() => Environment.TickCount64 < _followSuspendedUntilTick;

    private double GetCurrentExtrapolatedSeconds()
    {
        double totalSeconds = GetTotalSeconds();
        if (totalSeconds <= 0)
        {
            return 0.0;
        }

        if (!IsPlaying)
        {
            return Math.Clamp(Position.TotalSeconds, 0.0, totalSeconds);
        }

        double elapsed = Stopwatch.GetElapsedTime(_anchorTimestampTicks).TotalSeconds;
        double speed = PlaybackSpeed > 0 ? PlaybackSpeed : 1.0;
        return Math.Clamp(_anchorPositionSeconds + (elapsed * speed), 0.0, totalSeconds);
    }

    private double GetEffectivePlayheadSeconds(double totalSeconds)
    {
        if (_isDragging)
        {
            return Math.Clamp(_dragPositionSeconds, 0.0, totalSeconds);
        }

        return GetCurrentExtrapolatedSeconds();
    }

    private void ScheduleRender()
    {
        if (_renderScheduled)
        {
            return;
        }

        _renderScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _renderScheduled = false;
            InvalidateVisual();
        }, DispatcherPriority.Render);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        EnsureRenderResources();

        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        context.DrawRectangle(_backgroundBrush, null, new Rect(0, 0, width, height));

        double totalSeconds = GetTotalSeconds();
        if (totalSeconds <= 0 || PixelsPerSecond <= 0)
        {
            DrawEmptyState(context, width, height);
            return;
        }

        double pps = PixelsPerSecond;
        double offsetSeconds = ScrollOffsetSeconds;

        DrawRuler(context, width, totalSeconds, pps, offsetSeconds);
        DrawFilmstrip(context, width, totalSeconds, pps, offsetSeconds);

        if (Segments is { Count: > 0 } segments)
        {
            DrawSegments(context, width, totalSeconds, pps, offsetSeconds, segments);
            DrawMarkers(context, width, totalSeconds, pps, offsetSeconds, segments);
        }

        if (PendingSegmentStart is TimeSpan pendingStart)
        {
            DrawPendingStartMarker(context, width, totalSeconds, pps, offsetSeconds, pendingStart);
        }

        DrawPlayhead(context, width, height, totalSeconds, pps, offsetSeconds);

        if (_isSelectingZoomRange)
        {
            DrawZoomRangeSelection(context, width, totalSeconds, pps, offsetSeconds);
        }

        if (_isHovering)
        {
            DrawHoverLine(context, width, height);
        }
    }

    private void DrawEmptyState(DrawingContext dc, double width, double height)
    {
        var text = new FormattedText(
            "Ouvrez une vidéo pour commencer",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _emptyTypeface,
            13.0,
            _textMutedBrush);

        double x = Math.Max(0, (width - text.Width) / 2.0);
        double y = Math.Max(0, (height - text.Height) / 2.0);
        dc.DrawText(text, new Point(x, y));
    }

    private void DrawRuler(
        DrawingContext dc,
        double width,
        double totalSeconds,
        double pps,
        double offsetSeconds)
    {
        dc.DrawRectangle(_rulerBackgroundBrush, null, new Rect(0, 0, width, RulerHeight));
        double bottomLineY = RulerHeight - 0.5;
        dc.DrawLine(_borderPen, new Point(0, bottomLineY), new Point(width, bottomLineY));

        double majorStep = ChooseMajorStep(pps);
        int subdivisions = GetMinorSubdivisions(majorStep);
        double minorStep = majorStep / subdivisions;
        bool drawMinor = minorStep * pps >= MinMinorTickSpacingPx;
        bool includeHours = totalSeconds >= 3600.0;
        bool includeDecimals = majorStep < 1.0;

        double visibleEnd = Math.Min(totalSeconds, offsetSeconds + (width / pps));
        long firstIndex = Math.Max(0, (long)Math.Floor(offsetSeconds / minorStep));
        long lastIndex = (long)Math.Ceiling(visibleEnd / minorStep);

        for (long i = firstIndex; i <= lastIndex; i++)
        {
            double tickTime = i * minorStep;
            if (tickTime > totalSeconds + 0.0001)
            {
                break;
            }

            double x = (tickTime - offsetSeconds) * pps;
            if (x < -1.0 || x > width + 1.0)
            {
                continue;
            }

            double alignedX = Math.Round(x) + 0.5;
            bool isMajor = (i % subdivisions) == 0;

            if (isMajor)
            {
                dc.DrawLine(_majorTickPen, new Point(alignedX, RulerHeight - 9.0), new Point(alignedX, RulerHeight - 1.0));
                string labelText = FormatRulerLabel(tickTime, includeHours, includeDecimals);
                FormattedText ft = GetOrCreateLabelText(labelText);
                double labelX = alignedX + 4.0;
                if (labelX + ft.Width <= width - 2.0)
                {
                    dc.DrawText(ft, new Point(labelX, 3.0));
                }
            }
            else if (drawMinor)
            {
                dc.DrawLine(_minorTickPen, new Point(alignedX, RulerHeight - 5.0), new Point(alignedX, RulerHeight - 1.0));
            }
        }
    }

    private void DrawFilmstrip(
        DrawingContext dc,
        double width,
        double totalSeconds,
        double pps,
        double offsetSeconds)
    {
        IThumbnailProvider? provider = Thumbnails;
        IRangeThumbnailProvider? rangeProvider = RangeThumbnails;
        IThumbnailProvider? layoutSource = provider ?? rangeProvider;
        double tileWidth = layoutSource is not null && layoutSource.Layout.ThumbHeight > 0
            ? FilmstripHeight * layoutSource.Layout.ThumbWidth / layoutSource.Layout.ThumbHeight
            : FilmstripHeight * (16.0 / 9.0);

        if (tileWidth <= 1.0)
        {
            return;
        }

        double videoEndX = (totalSeconds - offsetSeconds) * pps;
        double visibleMediaWidth = Math.Min(width, videoEndX);
        if (visibleMediaWidth <= 0.0)
        {
            return;
        }

        int visibleTileCount = (int)Math.Ceiling(visibleMediaWidth / tileWidth) + 1;
        double offsetPx = offsetSeconds * pps;
        long firstTileIndex = Math.Max(0, (long)Math.Floor(offsetPx / tileWidth));

        using (dc.PushClip(new Rect(0, FilmstripTop, visibleMediaWidth, FilmstripHeight)))
        {
            for (int i = 0; i < visibleTileCount; i++)
            {
                long tileIndex = firstTileIndex + i;
                double x = (tileIndex * tileWidth) - offsetPx;
                if (x >= visibleMediaWidth)
                {
                    break;
                }

                double xCenter = x + (tileWidth / 2.0);
                double timeSec = Math.Clamp((xCenter / pps) + offsetSeconds, 0.0, totalSeconds);
                TimeSpan tileTime = TimeSpan.FromSeconds(timeSec);
                var tileRect = new Rect(x, FilmstripTop, tileWidth, FilmstripHeight);

                IImage? sheet = null;
                Rect sourceRect = default;
                bool hasTile = false;

                if (rangeProvider is not null
                    && rangeProvider.IsReady
                    && rangeProvider.ContainsTime(timeSec)
                    && rangeProvider.TryGetThumbnailTile(rangeProvider.IndexForTime(tileTime), out sheet, out sourceRect)
                    && sheet is not null)
                {
                    hasTile = true;
                }
                else if (provider is not null
                    && provider.TryGetThumbnailTile(provider.IndexForTime(tileTime), out sheet, out sourceRect)
                    && sheet is not null)
                {
                    hasTile = true;
                }

                if (hasTile && sheet is not null)
                {
                    dc.DrawImage(sheet, sourceRect, tileRect);
                    dc.DrawRectangle(null, _borderPen, tileRect);
                }
                else
                {
                    dc.DrawRectangle(_tilePlaceholderBrush, _borderPen, tileRect);
                }
            }
        }
    }

    private void DrawSegments(
        DrawingContext dc,
        double width,
        double totalSeconds,
        double pps,
        double offsetSeconds,
        IReadOnlyList<Segment> segments)
    {
        double visibleEnd = Math.Min(totalSeconds, offsetSeconds + (width / pps));
        int firstIdx = Math.Max(0, SegmentEditor.ContainingIndex(segments, TimeSpan.FromSeconds(offsetSeconds)) - 1);
        Guid? selectedId = SelectedSegmentId;

        for (int i = firstIdx; i < segments.Count; i++)
        {
            Segment seg = segments[i];
            double segStartSec = (_isDraggingMarker && i == _dragMarkerIndex + 1)
                ? _dragMarkerSeconds
                : seg.Start.TotalSeconds;
            double segEndSec = (_isDraggingMarker && i == _dragMarkerIndex)
                ? _dragMarkerSeconds
                : seg.End.TotalSeconds;

            if (segStartSec > visibleEnd && !(_isDraggingMarker && i <= _dragMarkerIndex + 1))
            {
                break;
            }

            if (segEndSec < offsetSeconds)
            {
                continue;
            }

            double x1 = Math.Max(0.0, (segStartSec - offsetSeconds) * pps);
            double x2 = Math.Min(width, (segEndSec - offsetSeconds) * pps);
            double segW = x2 - x1;
            if (segW <= 0.5)
            {
                continue;
            }

            if (!seg.Enabled)
            {
                dc.DrawRectangle(_disabledFilmstripScrimBrush, null, new Rect(x1, FilmstripTop, segW, FilmstripHeight));
            }

            IBrush fill = !seg.Enabled
                ? _segmentDisabledBrush
                : (i % 2 == 0 ? _segmentEvenBrush : _segmentOddBrush);

            bool isSelected = selectedId == seg.Id;
            IPen borderPen = isSelected ? _segmentSelectedPen : _segmentBorderPen;

            var bandRect = new Rect(x1, SegmentsBandTop, segW, SegmentsBandHeight);
            dc.DrawRectangle(fill, borderPen, bandRect);

            if (segW > 40.0 && !string.IsNullOrEmpty(seg.Name))
            {
                FormattedText ft = GetOrCreateSegmentLabelText(seg.Name, segW, seg.Enabled);
                double textY = SegmentsBandTop + Math.Max(0.0, (SegmentsBandHeight - ft.Height) / 2.0);
                double textX = x1 + 6.0;
                double maxAllowedWidth = Math.Max(0.0, segW - 10.0);

                if (ft.Width <= maxAllowedWidth)
                {
                    dc.DrawText(ft, new Point(textX, textY));
                }
                else
                {
                    using (dc.PushClip(new Rect(textX, SegmentsBandTop, maxAllowedWidth, SegmentsBandHeight)))
                    {
                        dc.DrawText(ft, new Point(textX, textY));
                    }
                }
            }
        }
    }

    private void DrawMarkers(
        DrawingContext dc,
        double width,
        double totalSeconds,
        double pps,
        double offsetSeconds,
        IReadOnlyList<Segment> segments)
    {
        int boundaryCount = segments.Count - 1;
        if (boundaryCount <= 0)
        {
            return;
        }

        double visibleEnd = Math.Min(totalSeconds, offsetSeconds + (width / pps));
        int firstIdx = Math.Max(0, SegmentEditor.ContainingIndex(segments, TimeSpan.FromSeconds(offsetSeconds)) - 1);
        double bandBottom = SegmentsBandTop + SegmentsBandHeight;

        for (int i = firstIdx; i < boundaryCount; i++)
        {
            double markerSec = (_isDraggingMarker && i == _dragMarkerIndex)
                ? _dragMarkerSeconds
                : segments[i].End.TotalSeconds;

            if (markerSec > visibleEnd + (8.0 / pps) && !(_isDraggingMarker && i == _dragMarkerIndex))
            {
                break;
            }

            double mx = (markerSec - offsetSeconds) * pps;
            if (mx < -8.0 || mx > width + 8.0)
            {
                continue;
            }

            double alignedX = Math.Round(mx) + 0.5;
            bool isSelected = SelectedMarkerIndex == i || (_isDraggingMarker && i == _dragMarkerIndex);
            IPen linePen = isSelected ? _markerSelectedPen : _markerPen;
            IBrush handleBrush = isSelected ? _markerSelectedBrush : _markerBrush;

            dc.DrawLine(linePen, new Point(alignedX, RulerHeight), new Point(alignedX, bandBottom));

            using (dc.PushTransform(Matrix.CreateTranslation(alignedX, FilmstripTop + 6.0)))
            {
                dc.DrawGeometry(handleBrush, _markerHandleOutlinePen, _markerDiamondGeometry);
            }
        }
    }

    private void DrawPendingStartMarker(
        DrawingContext dc,
        double width,
        double totalSeconds,
        double pps,
        double offsetSeconds,
        TimeSpan pendingStart)
    {
        double startSec = Math.Clamp(pendingStart.TotalSeconds, 0.0, totalSeconds);
        double x = (startSec - offsetSeconds) * pps;
        if (x < -10.0 || x > width + 10.0)
        {
            return;
        }

        double alignedX = Math.Round(x) + 0.5;
        double bandBottom = SegmentsBandTop + SegmentsBandHeight;
        dc.DrawLine(_pendingStartPen, new Point(alignedX, RulerHeight), new Point(alignedX, bandBottom));

        var flag = new StreamGeometry();
        using (StreamGeometryContext ctx = flag.Open())
        {
            ctx.BeginFigure(new Point(alignedX, RulerHeight), true);
            ctx.LineTo(new Point(alignedX + 10.0, RulerHeight + 5.0));
            ctx.LineTo(new Point(alignedX, RulerHeight + 10.0));
            ctx.EndFigure(true);
        }

        dc.DrawGeometry(_pendingStartBrush, _markerHandleOutlinePen, flag);
    }

    private void DrawPlayhead(
        DrawingContext dc,
        double width,
        double height,
        double totalSeconds,
        double pps,
        double offsetSeconds)
    {
        double posSec = GetEffectivePlayheadSeconds(totalSeconds);
        double x = (posSec - offsetSeconds) * pps;
        if (x < -8.0 || x > width + 8.0)
        {
            return;
        }

        double alignedX = Math.Round(x) + 0.5;
        dc.DrawLine(_playheadPen, new Point(alignedX, RulerHeight - 4.0), new Point(alignedX, height));

        var triangle = new StreamGeometry();
        using (StreamGeometryContext ctx = triangle.Open())
        {
            ctx.BeginFigure(new Point(alignedX - 6.0, 2.0), true);
            ctx.LineTo(new Point(alignedX + 6.0, 2.0));
            ctx.LineTo(new Point(alignedX, 13.0));
            ctx.EndFigure(true);
        }

        dc.DrawGeometry(_playheadBrush, null, triangle);
    }

    private void DrawHoverLine(DrawingContext dc, double width, double height)
    {
        double clampedX = Math.Clamp(_hoverX, 0.0, width);
        double alignedX = Math.Round(clampedX) + 0.5;
        dc.DrawLine(_hoverPen, new Point(alignedX, 0.0), new Point(alignedX, height));
    }

    private void DrawZoomRangeSelection(
        DrawingContext dc,
        double width,
        double totalSeconds,
        double pps,
        double offsetSeconds)
    {
        double t1 = Math.Clamp(Math.Min(_zoomSelectStartSeconds, _zoomSelectCurrentSeconds), 0.0, totalSeconds);
        double t2 = Math.Clamp(Math.Max(_zoomSelectStartSeconds, _zoomSelectCurrentSeconds), 0.0, totalSeconds);
        double x1 = Math.Clamp((t1 - offsetSeconds) * pps, 0.0, width);
        double x2 = Math.Clamp((t2 - offsetSeconds) * pps, 0.0, width);
        double selWidth = Math.Max(1.0, x2 - x1);

        var filmRect = new Rect(x1, RulerHeight, selWidth, FilmstripHeight + 2.0);
        dc.DrawRectangle(_zoomRangeFillBrush, null, filmRect);
        dc.DrawLine(_zoomRangeBorderPen, new Point(Math.Round(x1) + 0.5, RulerHeight), new Point(Math.Round(x1) + 0.5, SegmentsBandTop + SegmentsBandHeight));
        dc.DrawLine(_zoomRangeBorderPen, new Point(Math.Round(x2) + 0.5, RulerHeight), new Point(Math.Round(x2) + 0.5, SegmentsBandTop + SegmentsBandHeight));

        if (selWidth >= 24.0)
        {
            string rangeText = $"Zoom : {FormatRulerLabel(t1, totalSeconds >= 3600.0, true)} - {FormatRulerLabel(t2, totalSeconds >= 3600.0, true)}";
            var ft = new FormattedText(
                rangeText,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                _labelTypeface,
                10.5,
                _textPrimaryBrush);

            double badgeW = ft.Width + 10.0;
            double badgeH = ft.Height + 4.0;
            double badgeX = Math.Clamp(x1 + ((selWidth - badgeW) / 2.0), 4.0, Math.Max(4.0, width - badgeW - 4.0));
            double badgeY = FilmstripTop + 4.0;

            dc.DrawRectangle(_zoomRangeBadgeBrush, _zoomRangeBorderPen, new Rect(badgeX, badgeY, badgeW, badgeH), 3.0, 3.0);
            dc.DrawText(ft, new Point(badgeX + 5.0, badgeY + 2.0));
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        double totalSeconds = GetTotalSeconds();
        double width = Bounds.Width;
        if (totalSeconds <= 0 || width <= 0 || PixelsPerSecond <= 0)
        {
            return;
        }

        bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        if (ctrl)
        {
            double steps = e.Delta.Y;
            double factor = Math.Pow(WheelZoomFactor, steps);
            double mouseX = e.GetPosition(this).X;
            double timeUnderMouse = Math.Clamp((mouseX / PixelsPerSecond) + ScrollOffsetSeconds, 0.0, totalSeconds);
            ApplyZoomAtAnchor(factor, timeUnderMouse, mouseX);
            e.Handled = true;
            return;
        }

        double visibleDuration = width / PixelsPerSecond;
        double scrollStep = 0.10 * visibleDuration * (-e.Delta.Y);
        double newOffset = ClampOffset(ScrollOffsetSeconds + scrollStep, PixelsPerSecond, width);
        if (Math.Abs(newOffset - ScrollOffsetSeconds) > 0.0001)
        {
            NotifyUserNavigated();
            SetViewportValues(PixelsPerSecond, newOffset, IsFitToWindow);
            ScheduleRender();
            ViewportChanged?.Invoke(this, EventArgs.Empty);
        }

        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (GetTotalSeconds() <= 0 || PixelsPerSecond <= 0)
        {
            return;
        }

        PointerPoint pointerPoint = e.GetCurrentPoint(this);
        Point pos = pointerPoint.Position;
        _lastKeyModifiers = e.KeyModifiers;

        if (pointerPoint.Properties.IsRightButtonPressed)
        {
            MarkerSelectionRequested?.Invoke(this, null);
            TimeSpan outTime = SnapTimeToKeyframesIfEnabled(XToTime(pos.X), _lastKeyModifiers);
            _isHovering = true;
            _hoverX = pos.X;
            _dragPositionSeconds = outTime.TotalSeconds;
            InvalidateVisual();
            UpdateHoverPopup(pos, outTime);

            SeekCompleted?.Invoke(this, outTime);
            SetOutRequested?.Invoke(this, outTime);
            e.Handled = true;
            return;
        }

        if (!pointerPoint.Properties.IsLeftButtonPressed)
        {
            return;
        }

        // 0. Ctrl + maintenir clic + glisser = balayage de zoom sur une periode
        if ((_lastKeyModifiers & KeyModifiers.Control) != 0)
        {
            _isSelectingZoomRange = true;
            _zoomSelectStartX = Math.Clamp(pos.X, 0.0, Bounds.Width);
            _zoomSelectStartSeconds = XToTime(pos.X).TotalSeconds;
            _zoomSelectCurrentSeconds = _zoomSelectStartSeconds;
            _lastDragX = pos.X;
            _isHovering = true;
            _hoverX = pos.X;

            CapturePointer(e.Pointer);
            Cursor = ResizeHorizontalCursor;
            InvalidateVisual();
            UpdateHoverPopup(pos, TimeSpan.FromSeconds(_zoomSelectStartSeconds));
            e.Handled = true;
            return;
        }

        // 1. Priorite au hit-test des marqueurs
        int hitMarker = HitTestMarker(pos.X, pos.Y);
        if (hitMarker >= 0 && Segments is { Count: > 1 } segs && hitMarker < segs.Count - 1)
        {
            _isDraggingMarker = true;
            _hasMarkerDragMoved = false;
            _dragMarkerIndex = hitMarker;
            _dragMarkerStartX = pos.X;
            _lastDragX = pos.X;
            _dragMarkerOriginalSeconds = segs[hitMarker].End.TotalSeconds;
            _dragMarkerSeconds = _dragMarkerOriginalSeconds;
            _isHovering = true;
            _hoverX = pos.X;

            CapturePointer(e.Pointer);
            MarkerSelectionRequested?.Invoke(this, hitMarker);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        MarkerSelectionRequested?.Invoke(this, null);

        TimeSpan target = XToTime(pos.X);

        // 2. Clic / glisser sur la timeline (clic simple -> debut du segment ou selection, glisser -> scrub)
        _isDragging = true;
        _playheadDragStartX = pos.X;
        _hasPlayheadDragMoved = false;
        _isHovering = true;
        _hoverX = pos.X;
        _lastDragX = pos.X;
        _dragPositionSeconds = target.TotalSeconds;

        CapturePointer(e.Pointer);
        InvalidateVisual();
        UpdateHoverPopup(pos, target);

        SeekRequested?.Invoke(this, target);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        double totalSeconds = GetTotalSeconds();
        if (totalSeconds <= 0 || PixelsPerSecond <= 0)
        {
            _hoverPopup.Hide();
            return;
        }

        Point pos = e.GetPosition(this);
        _lastKeyModifiers = e.KeyModifiers;
        _isHovering = true;
        _hoverX = pos.X;

        if (_isSelectingZoomRange)
        {
            _lastDragX = pos.X;
            TimeSpan sweepTime = XToTime(pos.X);
            _zoomSelectCurrentSeconds = sweepTime.TotalSeconds;
            UpdateEdgeScrollTimer(pos.X);
            InvalidateVisual();
            UpdateHoverPopup(pos, sweepTime);
            return;
        }

        if (_isDraggingMarker)
        {
            _lastDragX = pos.X;
            if (!_hasMarkerDragMoved && Math.Abs(pos.X - _dragMarkerStartX) >= MarkerDragThresholdPx)
            {
                _hasMarkerDragMoved = true;
            }

            if (_hasMarkerDragMoved)
            {
                TimeSpan previewTime = ComputeSnappedAndClampedMarkerTime(pos.X, _lastKeyModifiers);
                _dragMarkerSeconds = previewTime.TotalSeconds;
                UpdateEdgeScrollTimer(pos.X);
                InvalidateVisual();
                UpdateHoverPopup(pos, previewTime);
                BoundaryDragPreview?.Invoke(this, (_dragMarkerIndex, previewTime));
            }
            else
            {
                InvalidateVisual();
            }

            return;
        }

        TimeSpan hoverTime = XToTime(pos.X);

        if (_isDragging)
        {
            _lastDragX = pos.X;
            if (!_hasPlayheadDragMoved && Math.Abs(pos.X - _playheadDragStartX) >= PlayheadDragThresholdPx)
            {
                _hasPlayheadDragMoved = true;
            }

            _dragPositionSeconds = hoverTime.TotalSeconds;
            UpdateEdgeScrollTimer(pos.X);
            SeekRequested?.Invoke(this, hoverTime);
        }
        else
        {
            UpdateCursorForPoint(pos);
        }

        InvalidateVisual();
        UpdateHoverPopup(pos, hoverTime);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        _lastKeyModifiers = e.KeyModifiers;

        if (_isSelectingZoomRange)
        {
            _edgeScrollTimer.Stop();
            Point upPos = e.GetPosition(this);
            TimeSpan endSweepTime = XToTime(upPos.X);
            double startSec = _zoomSelectStartSeconds;
            double endSec = endSweepTime.TotalSeconds;
            double pixelSpan = Math.Abs(upPos.X - _zoomSelectStartX);

            _isSelectingZoomRange = false;
            ReleasePointerCapture();

            if (pixelSpan >= 6.0 && Math.Abs(endSec - startSec) >= 0.05)
            {
                ZoomToRange(startSec, endSec);
            }

            if (!IsPointerOver)
            {
                _isHovering = false;
                _hoverPopup.Hide();
                if (!Equals(Cursor, HandCursor))
                {
                    Cursor = HandCursor;
                }
            }
            else
            {
                UpdateCursorForPoint(upPos);
                UpdateHoverPopup(upPos, endSweepTime);
            }

            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_isDraggingMarker)
        {
            _edgeScrollTimer.Stop();
            Point upPos = e.GetPosition(this);
            bool moved = _hasMarkerDragMoved;
            int idx = _dragMarkerIndex;
            TimeSpan finalTime = moved
                ? ComputeSnappedAndClampedMarkerTime(upPos.X, _lastKeyModifiers)
                : TimeSpan.FromSeconds(_dragMarkerOriginalSeconds);

            ResetMarkerDragState();
            ReleasePointerCapture();

            if (moved)
            {
                BoundaryDragCompleted?.Invoke(this, (idx, finalTime, false));
            }
            else
            {
                SeekCompleted?.Invoke(this, finalTime);
            }

            if (!IsPointerOver)
            {
                _isHovering = false;
                _hoverPopup.Hide();
            }
            else
            {
                UpdateCursorForPoint(upPos);
                UpdateHoverPopup(upPos, finalTime);
            }

            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (!_isDragging)
        {
            return;
        }

        _edgeScrollTimer.Stop();
        Point pos = e.GetPosition(this);
        TimeSpan finalSeek = XToTime(pos.X);
        bool dragged = _hasPlayheadDragMoved;

        _isDragging = false;
        _hasPlayheadDragMoved = false;
        _dragPositionSeconds = finalSeek.TotalSeconds;
        ReleasePointerCapture();

        if (!dragged)
        {
            if (pos.Y >= SegmentsBandTop && Segments is { Count: > 1 } segmentList)
            {
                int segIdx = SegmentEditor.ContainingIndex(segmentList, finalSeek);
                if (segIdx >= 0)
                {
                    SegmentSelectionRequested?.Invoke(this, segmentList[segIdx].Id);
                }

                SeekCompleted?.Invoke(this, finalSeek);
            }
            else
            {
                finalSeek = SnapTimeToKeyframesIfEnabled(finalSeek, _lastKeyModifiers);
                _dragPositionSeconds = finalSeek.TotalSeconds;
                SeekCompleted?.Invoke(this, finalSeek);
                SetInRequested?.Invoke(this, finalSeek);
            }
        }
        else
        {
            SeekCompleted?.Invoke(this, finalSeek);
        }

        if (!IsPointerOver)
        {
            _isHovering = false;
            _hoverPopup.Hide();
        }
        else
        {
            UpdateCursorForPoint(pos);
            UpdateHoverPopup(pos, finalSeek);
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        if (!_isDragging && !_isDraggingMarker && !_isSelectingZoomRange)
        {
            _isHovering = false;
            _hoverPopup.Hide();
            if (!Equals(Cursor, HandCursor))
            {
                Cursor = HandCursor;
            }

            InvalidateVisual();
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _capturedPointer = null;

        if (_isSelectingZoomRange)
        {
            _edgeScrollTimer.Stop();
            _isSelectingZoomRange = false;
            if (!IsPointerOver)
            {
                _isHovering = false;
                _hoverPopup.Hide();
            }

            if (!Equals(Cursor, HandCursor))
            {
                Cursor = HandCursor;
            }

            InvalidateVisual();
            return;
        }

        if (_isDraggingMarker)
        {
            _edgeScrollTimer.Stop();
            int idx = _dragMarkerIndex;
            TimeSpan original = TimeSpan.FromSeconds(_dragMarkerOriginalSeconds);
            ResetMarkerDragState();
            if (!IsPointerOver)
            {
                _isHovering = false;
                _hoverPopup.Hide();
            }

            InvalidateVisual();
            BoundaryDragCompleted?.Invoke(this, (idx, original, true));
            return;
        }

        if (_isDragging)
        {
            _edgeScrollTimer.Stop();
            _isDragging = false;
            _hasPlayheadDragMoved = false;
            if (!IsPointerOver)
            {
                _isHovering = false;
                _hoverPopup.Hide();
            }

            InvalidateVisual();
        }
    }

    private void CapturePointer(IPointer pointer)
    {
        _capturedPointer = pointer;
        pointer.Capture(this);
    }

    private void ReleasePointerCapture()
    {
        if (_capturedPointer is not null)
        {
            if (Equals(_capturedPointer.Captured, this))
            {
                _capturedPointer.Capture(null);
            }

            _capturedPointer = null;
        }
    }

    private int HitTestMarker(double mouseX, double mouseY)
    {
        if (mouseY < RulerHeight || Segments is not { Count: > 1 } segments || PixelsPerSecond <= 0)
        {
            return -1;
        }

        int bestIdx = -1;
        double bestDist = MarkerHitHalfWidthPx + 0.001;
        double offset = ScrollOffsetSeconds;
        double pps = PixelsPerSecond;

        for (int i = 0; i < segments.Count - 1; i++)
        {
            double mx = (segments[i].End.TotalSeconds - offset) * pps;
            double dist = Math.Abs(mouseX - mx);
            if (dist <= bestDist)
            {
                bestDist = dist;
                bestIdx = i;
            }
        }

        return bestIdx;
    }

    private void UpdateCursorForPoint(Point pos)
    {
        Cursor targetCursor = _isDraggingMarker || HitTestMarker(pos.X, pos.Y) >= 0
            ? ResizeHorizontalCursor
            : HandCursor;

        if (!Equals(Cursor, targetCursor))
        {
            Cursor = targetCursor;
        }
    }

    private TimeSpan SnapTimeToKeyframesIfEnabled(TimeSpan rawTime, KeyModifiers modifiers)
    {
        double rawSec = rawTime.TotalSeconds;
        bool altHeld = (modifiers & KeyModifiers.Alt) != 0;
        if (SnapToKeyframes && !altHeld && Keyframes is { Count: > 0 } kfs && PixelsPerSecond > 0)
        {
            double tolerance = Math.Min(SnapTolerancePx / PixelsPerSecond, MaxSnapToleranceSeconds);
            rawSec = KeyframeSnapper.Snap(rawSec, kfs, tolerance);
        }

        return TimeSpan.FromSeconds(Math.Clamp(rawSec, 0.0, GetTotalSeconds()));
    }

    private TimeSpan ComputeSnappedAndClampedMarkerTime(double mouseX, KeyModifiers modifiers)
    {
        if (Segments is not { Count: > 1 } segments || _dragMarkerIndex < 0 || _dragMarkerIndex >= segments.Count - 1)
        {
            return TimeSpan.Zero;
        }

        TimeSpan snapped = SnapTimeToKeyframesIfEnabled(XToTime(mouseX), modifiers);
        return SegmentEditor.ClampBoundary(segments, _dragMarkerIndex, snapped);
    }

    private void ResetMarkerDragState()
    {
        _isDraggingMarker = false;
        _hasMarkerDragMoved = false;
        _dragMarkerIndex = -1;
    }

    private void UpdateEdgeScrollTimer(double mouseX)
    {
        double width = Bounds.Width;
        bool nearEdge = width > EdgeScrollMarginPx * 2
            && (mouseX < EdgeScrollMarginPx || mouseX > width - EdgeScrollMarginPx);

        if (nearEdge && !_edgeScrollTimer.IsEnabled)
        {
            _edgeScrollTimer.Start();
        }
        else if (!nearEdge && _edgeScrollTimer.IsEnabled)
        {
            _edgeScrollTimer.Stop();
        }
    }

    private void OnEdgeScrollTick(object? sender, EventArgs e)
    {
        if ((!_isDragging && !_isSelectingZoomRange && !(_isDraggingMarker && _hasMarkerDragMoved))
            || Bounds.Width <= 0
            || PixelsPerSecond <= 0
            || GetTotalSeconds() <= 0)
        {
            _edgeScrollTimer.Stop();
            return;
        }

        double width = Bounds.Width;
        double deltaPx = 0.0;
        if (_lastDragX < EdgeScrollMarginPx)
        {
            deltaPx = -((EdgeScrollMarginPx - _lastDragX) / EdgeScrollMarginPx) * 14.0;
        }
        else if (_lastDragX > width - EdgeScrollMarginPx)
        {
            deltaPx = ((_lastDragX - (width - EdgeScrollMarginPx)) / EdgeScrollMarginPx) * 14.0;
        }

        if (Math.Abs(deltaPx) < 0.01)
        {
            return;
        }

        double deltaSeconds = deltaPx / PixelsPerSecond;
        double newOffset = ClampOffset(ScrollOffsetSeconds + deltaSeconds, PixelsPerSecond, width);
        if (Math.Abs(newOffset - ScrollOffsetSeconds) > 0.0001)
        {
            NotifyUserNavigated();
            SetViewportValues(PixelsPerSecond, newOffset, IsFitToWindow);
            ViewportChanged?.Invoke(this, EventArgs.Empty);
        }

        if (_isSelectingZoomRange)
        {
            TimeSpan sweepTime = XToTime(_lastDragX);
            _zoomSelectCurrentSeconds = sweepTime.TotalSeconds;
            InvalidateVisual();
            UpdateHoverPopup(new Point(_lastDragX, FilmstripTop), sweepTime);
            return;
        }

        if (_isDraggingMarker)
        {
            TimeSpan previewTime = ComputeSnappedAndClampedMarkerTime(_lastDragX, _lastKeyModifiers);
            _dragMarkerSeconds = previewTime.TotalSeconds;
            InvalidateVisual();
            UpdateHoverPopup(new Point(_lastDragX, FilmstripTop), previewTime);
            BoundaryDragPreview?.Invoke(this, (_dragMarkerIndex, previewTime));
            return;
        }

        TimeSpan target = XToTime(_lastDragX);
        _dragPositionSeconds = target.TotalSeconds;
        InvalidateVisual();
        UpdateHoverPopup(new Point(_lastDragX, FilmstripTop), target);
        SeekRequested?.Invoke(this, target);
    }

    private TimeSpan XToTime(double x)
    {
        double totalSeconds = GetTotalSeconds();
        if (totalSeconds <= 0 || PixelsPerSecond <= 0)
        {
            return TimeSpan.Zero;
        }

        double sec = Math.Clamp((x / PixelsPerSecond) + ScrollOffsetSeconds, 0.0, totalSeconds);
        return TimeSpan.FromSeconds(sec);
    }

    private void UpdateHoverPopup(Point mousePosition, TimeSpan time)
    {
        IImage? image = RangeThumbnails is { IsReady: true } rp && rp.ContainsTime(time.TotalSeconds)
            ? rp.GetThumbnail(time) ?? Thumbnails?.GetThumbnail(time)
            : Thumbnails?.GetThumbnail(time);
        _hoverPopup.Update(this, mousePosition, time, image, Duration);
    }

    private static double ChooseMajorStep(double pps)
    {
        foreach (double step in MajorStepCandidates)
        {
            if (step * pps >= MinMajorTickSpacingPx)
            {
                return step;
            }
        }

        return MajorStepCandidates[^1];
    }

    private static int GetMinorSubdivisions(double majorStep)
    {
        return majorStep switch
        {
            0.2 or 2.0 or 60.0 or 120.0 or 3600.0 or 7200.0 => 4,
            _ => 5
        };
    }

    private static string FormatRulerLabel(double seconds, bool includeHours, bool includeDecimals)
    {
        if (seconds < 0)
        {
            seconds = 0;
        }

        int tenths = (int)Math.Round(seconds * 10.0);
        int wholeSeconds = tenths / 10;
        int tenthDigit = tenths % 10;

        int hrs = wholeSeconds / 3600;
        int mins = (wholeSeconds % 3600) / 60;
        int secs = wholeSeconds % 60;

        if (includeHours)
        {
            return includeDecimals
                ? $"{hrs:D2}:{mins:D2}:{secs:D2}.{tenthDigit}"
                : $"{hrs:D2}:{mins:D2}:{secs:D2}";
        }

        int totalMins = wholeSeconds / 60;
        return includeDecimals
            ? $"{totalMins:D2}:{secs:D2}.{tenthDigit}"
            : $"{totalMins:D2}:{secs:D2}";
    }

    private FormattedText GetOrCreateLabelText(string text)
    {
        if (_labelCache.TryGetValue(text, out FormattedText? cached))
        {
            return cached;
        }

        if (_labelCache.Count >= MaxFormattedTextCacheEntries)
        {
            _labelCache.Clear();
        }

        var ft = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _labelTypeface,
            10.5,
            _textSecondaryBrush);

        _labelCache[text] = ft;
        return ft;
    }

    private FormattedText GetOrCreateSegmentLabelText(string name, double segmentWidth, bool enabled)
    {
        int widthBucket = Math.Max(40, ((int)segmentWidth / 16) * 16);
        var key = new SegmentLabelKey(name, widthBucket, enabled);

        if (_segmentLabelCache.TryGetValue(key, out FormattedText? cached))
        {
            return cached;
        }

        if (_segmentLabelCache.Count >= MaxFormattedTextCacheEntries)
        {
            _segmentLabelCache.Clear();
        }

        var ft = new FormattedText(
            name,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _emptyTypeface,
            11.0,
            enabled ? _textPrimaryBrush : _textMutedBrush)
        {
            MaxTextWidth = Math.Max(4.0, widthBucket - 12.0),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };

        _segmentLabelCache[key] = ft;
        return ft;
    }

    private void EnsureRenderResources()
    {
        if (_resourcesInitialized)
        {
            return;
        }

        _resourcesInitialized = true;

        _backgroundBrush = ResolveBrush("WindowBackgroundBrush", Color.FromRgb(0x12, 0x14, 0x17));
        _rulerBackgroundBrush = ResolveBrush("SurfaceBrush", Color.FromRgb(0x1A, 0x1D, 0x23));
        _tilePlaceholderBrush = ResolveBrush("SurfaceElevatedBrush", Color.FromRgb(0x22, 0x26, 0x2E));
        _textPrimaryBrush = ResolveBrush("TextPrimaryBrush", Color.FromRgb(0xED, 0xF0, 0xF5));
        _textSecondaryBrush = ResolveBrush("TextSecondaryBrush", Color.FromRgb(0x9A, 0xA3, 0xB2));
        _textMutedBrush = ResolveBrush("TextMutedBrush", Color.FromRgb(0x66, 0x6E, 0x7D));
        _playheadBrush = ResolveBrush("AccentBrush", Color.FromRgb(0x3B, 0x82, 0xF6));
        _segmentEvenBrush = ResolveBrush("AccentSubtleBrush", Color.FromRgb(0x1E, 0x3A, 0x5F));
        _segmentOddBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x24, 0x46, 0x70));
        _segmentDisabledBrush = ResolveBrush("DisabledBackgroundBrush", Color.FromRgb(0x18, 0x1B, 0x20));
        _disabledFilmstripScrimBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x99, 0x09, 0x0A, 0x0C));
        _markerBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
        _markerSelectedBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        _pendingStartBrush = ResolveBrush("SuccessBrush", Color.FromRgb(0x22, 0xC5, 0x5E));

        IImmutableBrush borderBrush = ResolveBrush("BorderBrush", Color.FromRgb(0x2E, 0x34, 0x40));
        IImmutableBrush selectedBorderBrush = ResolveBrush("AccentHoverBrush", Color.FromRgb(0x60, 0xA5, 0xFA));

        _borderPen = new ImmutablePen(borderBrush, 1.0);
        _majorTickPen = new ImmutablePen(_textSecondaryBrush, 1.0);
        _minorTickPen = new ImmutablePen(_textMutedBrush, 1.0);
        _playheadPen = new ImmutablePen(_playheadBrush, 2.0);
        _segmentBorderPen = new ImmutablePen(borderBrush, 1.0);
        _segmentSelectedPen = new ImmutablePen(selectedBorderBrush, 1.5);
        _markerPen = new ImmutablePen(_markerBrush, 2.0);
        _markerSelectedPen = new ImmutablePen(_markerSelectedBrush, 2.5);
        _markerHandleOutlinePen = new ImmutablePen(_backgroundBrush, 1.0);
        _pendingStartPen = new ImmutablePen(_pendingStartBrush, 2.0);
        _zoomRangeFillBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x48, 0x3B, 0x82, 0xF6));
        _zoomRangeBadgeBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xE6, 0x1A, 0x1D, 0x23));
        _zoomRangeBorderPen = new ImmutablePen(selectedBorderBrush, 1.5);

        IImmutableBrush hoverBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x99, 0xED, 0xF0, 0xF5));
        _hoverPen = new ImmutablePen(hoverBrush, 1.0);

        var diamond = new StreamGeometry();
        using (StreamGeometryContext ctx = diamond.Open())
        {
            ctx.BeginFigure(new Point(0.0, -5.0), true);
            ctx.LineTo(new Point(5.0, 0.0));
            ctx.LineTo(new Point(0.0, 5.0));
            ctx.LineTo(new Point(-5.0, 0.0));
            ctx.EndFigure(true);
        }

        _markerDiamondGeometry = diamond;

        _labelTypeface = new Typeface("Consolas");
        _emptyTypeface = new Typeface("Segoe UI");
    }

    private IImmutableBrush ResolveBrush(string resourceKey, Color fallbackColor)
    {
        if (this.TryFindResource(resourceKey, ActualThemeVariant, out object? found)
            && found is ISolidColorBrush solid)
        {
            return new ImmutableSolidColorBrush(solid.Color);
        }

        return new ImmutableSolidColorBrush(fallbackColor);
    }
}
