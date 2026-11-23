using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VideoSplitter.Editing;
using VideoSplitter.Models;
using VideoSplitter.Services;

namespace VideoSplitter.Controls;

public sealed class TimelineControl : FrameworkElement
{
    public const double TotalTimelineHeight = 135.0;
    private const double RulerHeight = 24.0;
    private const double FilmstripTop = 26.0;
    private const double FilmstripHeight = 80.0;
    private const double SegmentsBandTop = 108.0;
    private const double SegmentsBandHeight = 22.0;
    private const double MaxPixelsPerSecond = 500.0;
    private const double WheelZoomFactor = 1.25;
    private const double EdgeScrollMarginPx = 20.0;
    private const double MinMajorTickSpacingPx = 90.0;
    private const double MinMinorTickSpacingPx = 8.0;
    private const double MarkerHitHalfWidthPx = 6.0;
    private const double MarkerDragThresholdPx = 3.0;
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

    private readonly VisualCollection _children;
    private readonly DrawingVisual _backgroundVisual = new();
    private readonly DrawingVisual _segmentsVisual = new();
    private readonly DrawingVisual _playheadVisual = new();
    private readonly DrawingVisual _hoverVisual = new();
    private readonly ThumbnailHoverPopup _hoverPopup = new();
    private readonly DispatcherTimer _edgeScrollTimer;
    private readonly Dictionary<string, FormattedText> _labelCache = new(MaxFormattedTextCacheEntries);
    private readonly Dictionary<SegmentLabelKey, FormattedText> _segmentLabelCache = new(MaxFormattedTextCacheEntries);

    // Ressources graphiques resolues une fois et gelees
    private Brush _backgroundBrush = Brushes.Transparent;
    private Brush _rulerBackgroundBrush = Brushes.Transparent;
    private Brush _tilePlaceholderBrush = Brushes.Transparent;
    private Brush _textPrimaryBrush = Brushes.White;
    private Brush _textSecondaryBrush = Brushes.Gray;
    private Brush _textMutedBrush = Brushes.Gray;
    private Brush _playheadBrush = Brushes.DodgerBlue;
    private Brush _segmentEvenBrush = Brushes.DarkSlateBlue;
    private Brush _segmentOddBrush = Brushes.SlateBlue;
    private Brush _segmentDisabledBrush = Brushes.DimGray;
    private Brush _disabledFilmstripScrimBrush = Brushes.Transparent;
    private Brush _markerBrush = Brushes.Orange;
    private Brush _markerSelectedBrush = Brushes.Red;
    private Pen _borderPen = null!;
    private Pen _majorTickPen = null!;
    private Pen _minorTickPen = null!;
    private Pen _playheadPen = null!;
    private Pen _hoverPen = null!;
    private Pen _segmentBorderPen = null!;
    private Pen _segmentSelectedPen = null!;
    private Pen _markerPen = null!;
    private Pen _markerSelectedPen = null!;
    private Pen _markerHandleOutlinePen = null!;
    private StreamGeometry _markerDiamondGeometry = null!;
    private Typeface _labelTypeface = null!;
    private Typeface _emptyTypeface = null!;
    private bool _resourcesInitialized;

    // Etat de rendu groupe
    private bool _backgroundDirty = true;
    private bool _segmentsDirty = true;
    private bool _playheadDirty = true;
    private bool _renderScheduled;

    // Etat d'extrapolation du playhead
    private bool _isRenderingSubscribed;
    private double _anchorPositionSeconds;
    private long _anchorTimestampTicks;

    // Etat d'interaction souris / suivi
    private bool _isDragging;
    private bool _isDraggingMarker;
    private bool _hasMarkerDragMoved;
    private int _dragMarkerIndex = -1;
    private double _dragMarkerStartX;
    private double _dragMarkerOriginalSeconds;
    private double _dragMarkerSeconds;
    private bool _isHovering;
    private double _hoverX;
    private double _lastDragX;
    private double _dragPositionSeconds;
    private long _followSuspendedUntilTick;
    private bool _isUpdatingViewportProps;

    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(
        nameof(Duration),
        typeof(TimeSpan),
        typeof(TimelineControl),
        new FrameworkPropertyMetadata(TimeSpan.Zero, OnDurationPropertyChanged));

    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position),
        typeof(TimeSpan),
        typeof(TimelineControl),
        new FrameworkPropertyMetadata(TimeSpan.Zero, OnPositionPropertyChanged));

    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(
        nameof(IsPlaying),
        typeof(bool),
        typeof(TimelineControl),
        new FrameworkPropertyMetadata(false, OnIsPlayingPropertyChanged));

    public static readonly DependencyProperty PlaybackSpeedProperty = DependencyProperty.Register(
        nameof(PlaybackSpeed),
        typeof(double),
        typeof(TimelineControl),
        new FrameworkPropertyMetadata(1.0, OnPlaybackSpeedPropertyChanged));

    public static readonly DependencyProperty ThumbnailsProperty = DependencyProperty.Register(
        nameof(Thumbnails),
        typeof(IThumbnailProvider),
        typeof(TimelineControl),
        new FrameworkPropertyMetadata(null, OnThumbnailsPropertyChanged));

    public static readonly DependencyProperty PixelsPerSecondProperty = DependencyProperty.Register(
        nameof(PixelsPerSecond),
        typeof(double),
        typeof(TimelineControl),
        new FrameworkPropertyMetadata(
            10.0,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnViewportPropertyChanged));

    public static readonly DependencyProperty ScrollOffsetSecondsProperty = DependencyProperty.Register(
        nameof(ScrollOffsetSeconds),
        typeof(double),
        typeof(TimelineControl),
        new FrameworkPropertyMetadata(
            0.0,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnViewportPropertyChanged));

    public static readonly DependencyProperty IsFitToWindowProperty = DependencyProperty.Register(
        nameof(IsFitToWindow),
        typeof(bool),
        typeof(TimelineControl),
        new FrameworkPropertyMetadata(
            true,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnIsFitToWindowPropertyChanged));

    public static readonly DependencyProperty FollowPlaybackProperty = DependencyProperty.Register(
        nameof(FollowPlayback),
        typeof(bool),
        typeof(TimelineControl),
        new FrameworkPropertyMetadata(true));

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments),
        typeof(IReadOnlyList<Segment>),
        typeof(TimelineControl),
        new FrameworkPropertyMetadata(null, OnSegmentsPropertyChanged));

    public static readonly DependencyProperty SelectedSegmentIdProperty = DependencyProperty.Register(
        nameof(SelectedSegmentId),
        typeof(Guid?),
        typeof(TimelineControl),
        new FrameworkPropertyMetadata(null, OnSegmentSelectionPropertyChanged));

    public static readonly DependencyProperty SelectedMarkerIndexProperty = DependencyProperty.Register(
        nameof(SelectedMarkerIndex),
        typeof(int?),
        typeof(TimelineControl),
        new FrameworkPropertyMetadata(null, OnSegmentSelectionPropertyChanged));

    public static readonly DependencyProperty KeyframesProperty = DependencyProperty.Register(
        nameof(Keyframes),
        typeof(IReadOnlyList<double>),
        typeof(TimelineControl),
        new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty SnapToKeyframesProperty = DependencyProperty.Register(
        nameof(SnapToKeyframes),
        typeof(bool),
        typeof(TimelineControl),
        new FrameworkPropertyMetadata(true));

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

    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public double PlaybackSpeed
    {
        get => (double)GetValue(PlaybackSpeedProperty);
        set => SetValue(PlaybackSpeedProperty, value);
    }

    public IThumbnailProvider? Thumbnails
    {
        get => (IThumbnailProvider?)GetValue(ThumbnailsProperty);
        set => SetValue(ThumbnailsProperty, value);
    }

    public double PixelsPerSecond
    {
        get => (double)GetValue(PixelsPerSecondProperty);
        set => SetValue(PixelsPerSecondProperty, value);
    }

    public double ScrollOffsetSeconds
    {
        get => (double)GetValue(ScrollOffsetSecondsProperty);
        set => SetValue(ScrollOffsetSecondsProperty, value);
    }

    public bool IsFitToWindow
    {
        get => (bool)GetValue(IsFitToWindowProperty);
        set => SetValue(IsFitToWindowProperty, value);
    }

    public bool FollowPlayback
    {
        get => (bool)GetValue(FollowPlaybackProperty);
        set => SetValue(FollowPlaybackProperty, value);
    }

    public IReadOnlyList<Segment>? Segments
    {
        get => (IReadOnlyList<Segment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public Guid? SelectedSegmentId
    {
        get => (Guid?)GetValue(SelectedSegmentIdProperty);
        set => SetValue(SelectedSegmentIdProperty, value);
    }

    public int? SelectedMarkerIndex
    {
        get => (int?)GetValue(SelectedMarkerIndexProperty);
        set => SetValue(SelectedMarkerIndexProperty, value);
    }

    public IReadOnlyList<double>? Keyframes
    {
        get => (IReadOnlyList<double>?)GetValue(KeyframesProperty);
        set => SetValue(KeyframesProperty, value);
    }

    public bool SnapToKeyframes
    {
        get => (bool)GetValue(SnapToKeyframesProperty);
        set => SetValue(SnapToKeyframesProperty, value);
    }

    public event EventHandler<TimeSpan>? SeekRequested;
    public event EventHandler<TimeSpan>? SeekCompleted;
    public event EventHandler? ViewportChanged;
    public event EventHandler<Guid?>? SegmentSelectionRequested;
    public event EventHandler<int?>? MarkerSelectionRequested;
    public event EventHandler<(int Index, TimeSpan Time)>? BoundaryDragPreview;
    public event EventHandler<(int Index, TimeSpan Time, bool Cancelled)>? BoundaryDragCompleted;

    public TimelineControl()
    {
        _children = new VisualCollection(this)
        {
            _backgroundVisual,
            _segmentsVisual,
            _playheadVisual,
            _hoverVisual
        };

        Cursor = Cursors.Hand;
        ClipToBounds = true;
        SnapsToDevicePixels = true;

        _edgeScrollTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _edgeScrollTimer.Tick += OnEdgeScrollTick;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override int VisualChildrenCount => _children.Count;

    protected override Visual GetVisualChild(int index) => _children[index];

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 0.0 : availableSize.Width;
        return new Size(width, TotalTimelineHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        SyncViewportToSize(finalSize.Width);
        InvalidateBackground();
        return finalSize;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _labelCache.Clear();
        _segmentLabelCache.Clear();
        InvalidateBackground();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureRenderResources();
        SyncViewportToSize(ActualWidth);
        UpdateRenderingSubscription();
        InvalidateBackground();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _edgeScrollTimer.Stop();
        _hoverPopup.Hide();
        UnsubscribeRendering();
    }

    private static void OnDurationPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimelineControl ctrl)
        {
            ctrl.HandleDurationChanged();
        }
    }

    private static void OnPositionPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimelineControl ctrl)
        {
            ctrl.HandlePositionChanged();
        }
    }

    private static void OnIsPlayingPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimelineControl ctrl)
        {
            ctrl.HandleIsPlayingChanged();
        }
    }

    private static void OnPlaybackSpeedPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimelineControl ctrl)
        {
            ctrl.ReanchorPlayhead(ctrl.GetCurrentExtrapolatedSeconds());
        }
    }

    private static void OnThumbnailsPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimelineControl ctrl)
        {
            if (e.OldValue is IThumbnailProvider oldProvider)
            {
                oldProvider.ThumbnailsChanged -= ctrl.OnProviderThumbnailsChanged;
            }

            if (e.NewValue is IThumbnailProvider newProvider)
            {
                newProvider.ThumbnailsChanged += ctrl.OnProviderThumbnailsChanged;
            }

            ctrl.InvalidateBackground();
        }
    }

    private static void OnViewportPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimelineControl ctrl && !ctrl._isUpdatingViewportProps)
        {
            ctrl.ClampViewportProps();
            ctrl.InvalidateBackground();
            ctrl.ViewportChanged?.Invoke(ctrl, EventArgs.Empty);
        }
    }

    private static void OnIsFitToWindowPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimelineControl ctrl && !ctrl._isUpdatingViewportProps && (bool)e.NewValue)
        {
            ctrl.SyncViewportToSize(ctrl.ActualWidth);
            ctrl.InvalidateBackground();
        }
    }

    private static void OnSegmentsPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimelineControl ctrl)
        {
            if (ctrl._isDraggingMarker)
            {
                ctrl.ResetMarkerDragState();
            }

            ctrl.InvalidateSegments();
        }
    }

    private static void OnSegmentSelectionPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimelineControl ctrl)
        {
            ctrl.InvalidateSegments();
        }
    }

    private void OnProviderThumbnailsChanged(object? sender, EventArgs e)
    {
        InvalidateBackground();
    }

    private void HandleDurationChanged()
    {
        _followSuspendedUntilTick = 0;
        ReanchorPlayhead(Math.Clamp(Position.TotalSeconds, 0.0, GetTotalSeconds()));
        SyncViewportToSize(ActualWidth);
        UpdateRenderingSubscription();
        InvalidateBackground();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandlePositionChanged()
    {
        double totalSec = GetTotalSeconds();
        double posSec = Math.Clamp(Position.TotalSeconds, 0.0, totalSec);
        ReanchorPlayhead(posSec);

        if (!_isDragging && !_isDraggingMarker)
        {
            InvalidatePlayhead();
        }
    }

    private void HandleIsPlayingChanged()
    {
        double currentSec = GetCurrentExtrapolatedSeconds();
        ReanchorPlayhead(currentSec);
        UpdateRenderingSubscription();
        InvalidatePlayhead();
    }

    private void ReanchorPlayhead(double positionSeconds)
    {
        _anchorPositionSeconds = Math.Clamp(positionSeconds, 0.0, GetTotalSeconds());
        _anchorTimestampTicks = Stopwatch.GetTimestamp();
    }

    private void UpdateRenderingSubscription()
    {
        if (IsPlaying && Duration > TimeSpan.Zero && IsLoaded)
        {
            if (!_isRenderingSubscribed)
            {
                CompositionTarget.Rendering += OnCompositionTargetRendering;
                _isRenderingSubscribed = true;
            }
        }
        else
        {
            UnsubscribeRendering();
        }
    }

    private void UnsubscribeRendering()
    {
        if (_isRenderingSubscribed)
        {
            CompositionTarget.Rendering -= OnCompositionTargetRendering;
            _isRenderingSubscribed = false;
        }
    }

    private void OnCompositionTargetRendering(object? sender, EventArgs e)
    {
        if (!IsPlaying || Duration <= TimeSpan.Zero)
        {
            UnsubscribeRendering();
            return;
        }

        double currentPos = GetCurrentExtrapolatedSeconds();

        if (FollowPlayback && !IsFollowSuspended() && ActualWidth > 0 && PixelsPerSecond > 0 && !IsFitToWindow)
        {
            double visibleDuration = ActualWidth / PixelsPerSecond;
            double offset = ScrollOffsetSeconds;
            if (currentPos > offset + visibleDuration || currentPos < offset)
            {
                double pagedOffset = ClampOffset(currentPos - (0.10 * visibleDuration), PixelsPerSecond, ActualWidth);
                if (Math.Abs(pagedOffset - offset) > 0.0001)
                {
                    SetViewportValues(PixelsPerSecond, pagedOffset, IsFitToWindow);
                    InvalidateBackground();
                    ViewportChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        RenderPlayheadLayer();
    }

    public void NotifyUserNavigated()
    {
        _followSuspendedUntilTick = Environment.TickCount64 + FollowSuspendDurationMs;
    }

    public bool CancelActiveDrag()
    {
        if (_isDraggingMarker)
        {
            _edgeScrollTimer.Stop();
            int idx = _dragMarkerIndex;
            TimeSpan original = TimeSpan.FromSeconds(_dragMarkerOriginalSeconds);
            ResetMarkerDragState();
            ReleaseMouseCapture();
            _hoverPopup.Hide();

            RenderSegmentsLayer();
            RenderHoverLayer();
            UpdateCursorForPoint(Mouse.GetPosition(this));
            BoundaryDragCompleted?.Invoke(this, (idx, original, true));
            return true;
        }

        if (_isDragging)
        {
            _edgeScrollTimer.Stop();
            _isDragging = false;
            ReleaseMouseCapture();
            RenderPlayheadLayer();
            RenderHoverLayer();
            return true;
        }

        return false;
    }

    public void ZoomByFactorCentered(double factor)
    {
        double totalSeconds = GetTotalSeconds();
        double width = ActualWidth;
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
        double width = ActualWidth;
        if (totalSeconds <= 0 || width <= 0)
        {
            return;
        }

        double minPps = GetMinPixelsPerSecond(width, totalSeconds);
        double currentPps = PixelsPerSecond > 0 ? PixelsPerSecond : minPps;
        double newPps = Math.Clamp(currentPps * factor, minPps, MaxPixelsPerSecond);
        double newOffset = ClampOffset(anchorTime - (anchorX / newPps), newPps, width);

        NotifyUserNavigated();
        SetViewportValues(newPps, newOffset, isFit: false);
        InvalidateBackground();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
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
        double width = ActualWidth;
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

    private void InvalidateBackground()
    {
        _backgroundDirty = true;
        _segmentsDirty = true;
        _playheadDirty = true;
        ScheduleRender();
    }

    private void InvalidateSegments()
    {
        _segmentsDirty = true;
        _playheadDirty = true;
        ScheduleRender();
    }

    private void InvalidatePlayhead()
    {
        _playheadDirty = true;
        ScheduleRender();
    }

    private void ScheduleRender()
    {
        if (_renderScheduled)
        {
            return;
        }

        _renderScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, FlushPendingRenders);
    }

    private void FlushPendingRenders()
    {
        _renderScheduled = false;

        if (_backgroundDirty)
        {
            _backgroundDirty = false;
            RenderBackgroundLayer();
        }

        if (_segmentsDirty)
        {
            _segmentsDirty = false;
            RenderSegmentsLayer();
        }

        if (_playheadDirty)
        {
            _playheadDirty = false;
            RenderPlayheadLayer();
        }
    }

    private void RenderBackgroundLayer()
    {
        EnsureRenderResources();

        using DrawingContext dc = _backgroundVisual.RenderOpen();

        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        // Remplissage complet pour garantir le hit-test sur toute la surface
        dc.DrawRectangle(_backgroundBrush, null, new Rect(0, 0, width, height));

        double totalSeconds = GetTotalSeconds();
        if (totalSeconds <= 0 || PixelsPerSecond <= 0)
        {
            DrawEmptyState(dc, width, height);
            return;
        }

        double pps = PixelsPerSecond;
        double offsetSeconds = ScrollOffsetSeconds;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        DrawRuler(dc, width, totalSeconds, pps, offsetSeconds, pixelsPerDip);
        DrawFilmstrip(dc, width, totalSeconds, pps, offsetSeconds);
    }

    private void RenderSegmentsLayer()
    {
        EnsureRenderResources();

        using DrawingContext dc = _segmentsVisual.RenderOpen();

        double width = ActualWidth;
        double height = ActualHeight;
        double totalSeconds = GetTotalSeconds();
        if (width <= 0 || height <= 0 || totalSeconds <= 0 || PixelsPerSecond <= 0)
        {
            return;
        }

        if (Segments is not { Count: > 0 } segments)
        {
            return;
        }

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        DrawSegments(dc, width, totalSeconds, PixelsPerSecond, ScrollOffsetSeconds, segments, pixelsPerDip);
        DrawMarkers(dc, width, totalSeconds, PixelsPerSecond, ScrollOffsetSeconds, segments);
    }

    private void RenderPlayheadLayer()
    {
        EnsureRenderResources();

        using DrawingContext dc = _playheadVisual.RenderOpen();

        double width = ActualWidth;
        double height = ActualHeight;
        double totalSeconds = GetTotalSeconds();
        if (width <= 0 || height <= 0 || totalSeconds <= 0 || PixelsPerSecond <= 0)
        {
            return;
        }

        DrawPlayhead(dc, width, height, totalSeconds, PixelsPerSecond, ScrollOffsetSeconds);
    }

    private void RenderHoverLayer()
    {
        EnsureRenderResources();

        using DrawingContext dc = _hoverVisual.RenderOpen();

        double width = ActualWidth;
        double height = ActualHeight;
        if (!_isHovering || width <= 0 || height <= 0 || GetTotalSeconds() <= 0)
        {
            return;
        }

        DrawHoverLine(dc, width, height);
    }

    private void DrawEmptyState(DrawingContext dc, double width, double height)
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = new FormattedText(
            "Ouvrez une vidéo pour commencer",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _emptyTypeface,
            13.0,
            _textMutedBrush,
            pixelsPerDip);

        double x = Math.Max(0, (width - text.Width) / 2.0);
        double y = Math.Max(0, (height - text.Height) / 2.0);
        dc.DrawText(text, new Point(x, y));
    }

    private void DrawRuler(
        DrawingContext dc,
        double width,
        double totalSeconds,
        double pps,
        double offsetSeconds,
        double pixelsPerDip)
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
                FormattedText ft = GetOrCreateLabelText(labelText, pixelsPerDip);
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
        double tileWidth = provider is not null && provider.Layout.ThumbHeight > 0
            ? FilmstripHeight * provider.Layout.ThumbWidth / provider.Layout.ThumbHeight
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

        dc.PushClip(new RectangleGeometry(new Rect(0, FilmstripTop, visibleMediaWidth, FilmstripHeight)));
        try
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

                Brush? tileBrush = null;
                if (provider is not null)
                {
                    int thumbIdx = provider.IndexForTime(TimeSpan.FromSeconds(timeSec));
                    tileBrush = provider.GetThumbnailBrush(thumbIdx);
                }

                var tileRect = new Rect(x, FilmstripTop, tileWidth, FilmstripHeight);
                dc.DrawRectangle(tileBrush ?? _tilePlaceholderBrush, _borderPen, tileRect);
            }
        }
        finally
        {
            dc.Pop();
        }
    }

    private void DrawSegments(
        DrawingContext dc,
        double width,
        double totalSeconds,
        double pps,
        double offsetSeconds,
        IReadOnlyList<Segment> segments,
        double pixelsPerDip)
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

            Brush fill = !seg.Enabled
                ? _segmentDisabledBrush
                : (i % 2 == 0 ? _segmentEvenBrush : _segmentOddBrush);

            bool isSelected = selectedId == seg.Id;
            Pen borderPen = isSelected ? _segmentSelectedPen : _segmentBorderPen;

            var bandRect = new Rect(x1, SegmentsBandTop, segW, SegmentsBandHeight);
            dc.DrawRectangle(fill, borderPen, bandRect);

            if (segW > 40.0 && !string.IsNullOrEmpty(seg.Name))
            {
                FormattedText ft = GetOrCreateSegmentLabelText(seg.Name, segW, seg.Enabled, pixelsPerDip);
                double textY = SegmentsBandTop + Math.Max(0.0, (SegmentsBandHeight - ft.Height) / 2.0);
                double textX = x1 + 6.0;
                double maxAllowedWidth = Math.Max(0.0, segW - 10.0);
                if (ft.Width <= maxAllowedWidth)
                {
                    dc.DrawText(ft, new Point(textX, textY));
                }
                else
                {
                    dc.PushClip(new RectangleGeometry(new Rect(textX, SegmentsBandTop, maxAllowedWidth, SegmentsBandHeight)));
                    try
                    {
                        dc.DrawText(ft, new Point(textX, textY));
                    }
                    finally
                    {
                        dc.Pop();
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
            Pen linePen = isSelected ? _markerSelectedPen : _markerPen;
            Brush handleBrush = isSelected ? _markerSelectedBrush : _markerBrush;

            dc.DrawLine(linePen, new Point(alignedX, RulerHeight), new Point(alignedX, bandBottom));

            dc.PushTransform(new TranslateTransform(alignedX, FilmstripTop + 6.0));
            try
            {
                dc.DrawGeometry(handleBrush, _markerHandleOutlinePen, _markerDiamondGeometry);
            }
            finally
            {
                dc.Pop();
            }
        }
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
            ctx.BeginFigure(new Point(alignedX - 6.0, 2.0), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(alignedX + 6.0, 2.0), isStroked: false, isSmoothJoin: false);
            ctx.LineTo(new Point(alignedX, 13.0), isStroked: false, isSmoothJoin: false);
        }

        triangle.Freeze();
        dc.DrawGeometry(_playheadBrush, null, triangle);
    }

    private void DrawHoverLine(DrawingContext dc, double width, double height)
    {
        double clampedX = Math.Clamp(_hoverX, 0.0, width);
        double alignedX = Math.Round(clampedX) + 0.5;
        dc.DrawLine(_hoverPen, new Point(alignedX, 0.0), new Point(alignedX, height));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        double totalSeconds = GetTotalSeconds();
        double width = ActualWidth;
        if (totalSeconds <= 0 || width <= 0 || PixelsPerSecond <= 0)
        {
            return;
        }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (ctrl)
        {
            double steps = e.Delta / 120.0;
            double factor = Math.Pow(WheelZoomFactor, steps);
            double mouseX = e.GetPosition(this).X;
            double timeUnderMouse = Math.Clamp((mouseX / PixelsPerSecond) + ScrollOffsetSeconds, 0.0, totalSeconds);
            ApplyZoomAtAnchor(factor, timeUnderMouse, mouseX);
            e.Handled = true;
            return;
        }

        double visibleDuration = width / PixelsPerSecond;
        double scrollStep = 0.10 * visibleDuration * (-e.Delta / 120.0);
        double newOffset = ClampOffset(ScrollOffsetSeconds + scrollStep, PixelsPerSecond, width);
        if (Math.Abs(newOffset - ScrollOffsetSeconds) > 0.0001)
        {
            NotifyUserNavigated();
            SetViewportValues(PixelsPerSecond, newOffset, IsFitToWindow);
            InvalidateBackground();
            ViewportChanged?.Invoke(this, EventArgs.Empty);
        }

        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (GetTotalSeconds() <= 0 || PixelsPerSecond <= 0)
        {
            return;
        }

        Point pos = e.GetPosition(this);

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

            CaptureMouse();
            MarkerSelectionRequested?.Invoke(this, hitMarker);
            RenderSegmentsLayer();
            RenderHoverLayer();
            e.Handled = true;
            return;
        }

        MarkerSelectionRequested?.Invoke(this, null);

        TimeSpan target = XToTime(pos.X);

        // 2. Bande segments : selectionner le segment clique
        if (pos.Y >= SegmentsBandTop && Segments is { Count: > 0 } segmentList)
        {
            int segIdx = SegmentEditor.ContainingIndex(segmentList, target);
            if (segIdx >= 0)
            {
                SegmentSelectionRequested?.Invoke(this, segmentList[segIdx].Id);
            }
        }

        // 3. Regle / filmstrip / bande segments : deplacer le playhead
        _isDragging = true;
        _isHovering = true;
        _hoverX = pos.X;
        _lastDragX = pos.X;
        _dragPositionSeconds = target.TotalSeconds;

        CaptureMouse();
        RenderPlayheadLayer();
        RenderHoverLayer();
        UpdateHoverPopup(pos, target);

        SeekRequested?.Invoke(this, target);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        double totalSeconds = GetTotalSeconds();
        if (totalSeconds <= 0 || PixelsPerSecond <= 0)
        {
            _hoverPopup.Hide();
            return;
        }

        Point pos = e.GetPosition(this);
        _isHovering = true;
        _hoverX = pos.X;

        if (_isDraggingMarker)
        {
            _lastDragX = pos.X;
            if (!_hasMarkerDragMoved && Math.Abs(pos.X - _dragMarkerStartX) >= MarkerDragThresholdPx)
            {
                _hasMarkerDragMoved = true;
            }

            if (_hasMarkerDragMoved)
            {
                TimeSpan previewTime = ComputeSnappedAndClampedMarkerTime(pos.X);
                _dragMarkerSeconds = previewTime.TotalSeconds;
                UpdateEdgeScrollTimer(pos.X);
                RenderSegmentsLayer();
                UpdateHoverPopup(pos, previewTime);
                BoundaryDragPreview?.Invoke(this, (_dragMarkerIndex, previewTime));
            }

            RenderHoverLayer();
            return;
        }

        TimeSpan hoverTime = XToTime(pos.X);

        if (_isDragging)
        {
            _lastDragX = pos.X;
            _dragPositionSeconds = hoverTime.TotalSeconds;
            UpdateEdgeScrollTimer(pos.X);
            RenderPlayheadLayer();
            SeekRequested?.Invoke(this, hoverTime);
        }
        else
        {
            UpdateCursorForPoint(pos);
        }

        RenderHoverLayer();
        UpdateHoverPopup(pos, hoverTime);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_isDraggingMarker)
        {
            _edgeScrollTimer.Stop();
            Point upPos = e.GetPosition(this);
            bool moved = _hasMarkerDragMoved;
            int idx = _dragMarkerIndex;
            TimeSpan finalTime = moved
                ? ComputeSnappedAndClampedMarkerTime(upPos.X)
                : TimeSpan.FromSeconds(_dragMarkerOriginalSeconds);

            ResetMarkerDragState();
            ReleaseMouseCapture();

            if (moved)
            {
                BoundaryDragCompleted?.Invoke(this, (idx, finalTime, false));
            }
            else
            {
                SeekCompleted?.Invoke(this, finalTime);
            }

            if (!IsMouseOver)
            {
                _isHovering = false;
                _hoverPopup.Hide();
            }
            else
            {
                UpdateCursorForPoint(upPos);
                UpdateHoverPopup(upPos, finalTime);
            }

            RenderSegmentsLayer();
            RenderHoverLayer();
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

        _isDragging = false;
        _dragPositionSeconds = finalSeek.TotalSeconds;
        ReleaseMouseCapture();

        SeekCompleted?.Invoke(this, finalSeek);

        if (!IsMouseOver)
        {
            _isHovering = false;
            _hoverPopup.Hide();
        }
        else
        {
            UpdateCursorForPoint(pos);
            UpdateHoverPopup(pos, finalSeek);
        }

        RenderPlayheadLayer();
        RenderHoverLayer();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);

        if (!_isDragging && !_isDraggingMarker)
        {
            _isHovering = false;
            _hoverPopup.Hide();
            Cursor = Cursors.Hand;
            RenderHoverLayer();
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        if (_isDraggingMarker)
        {
            _edgeScrollTimer.Stop();
            int idx = _dragMarkerIndex;
            TimeSpan original = TimeSpan.FromSeconds(_dragMarkerOriginalSeconds);
            ResetMarkerDragState();
            if (!IsMouseOver)
            {
                _isHovering = false;
                _hoverPopup.Hide();
            }

            RenderSegmentsLayer();
            RenderHoverLayer();
            BoundaryDragCompleted?.Invoke(this, (idx, original, true));
            return;
        }

        if (_isDragging)
        {
            _edgeScrollTimer.Stop();
            _isDragging = false;
            if (!IsMouseOver)
            {
                _isHovering = false;
                _hoverPopup.Hide();
            }

            RenderPlayheadLayer();
            RenderHoverLayer();
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
        Cursor = _isDraggingMarker || HitTestMarker(pos.X, pos.Y) >= 0
            ? Cursors.SizeWE
            : Cursors.Hand;
    }

    private TimeSpan ComputeSnappedAndClampedMarkerTime(double mouseX)
    {
        if (Segments is not { Count: > 1 } segments || _dragMarkerIndex < 0 || _dragMarkerIndex >= segments.Count - 1)
        {
            return TimeSpan.Zero;
        }

        double rawSec = XToTime(mouseX).TotalSeconds;
        bool altHeld = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
        if (SnapToKeyframes && !altHeld && Keyframes is { Count: > 0 } kfs && PixelsPerSecond > 0)
        {
            double tolerance = Math.Min(SnapTolerancePx / PixelsPerSecond, MaxSnapToleranceSeconds);
            rawSec = KeyframeSnapper.Snap(rawSec, kfs, tolerance);
        }

        return SegmentEditor.ClampBoundary(segments, _dragMarkerIndex, TimeSpan.FromSeconds(rawSec));
    }

    private void ResetMarkerDragState()
    {
        _isDraggingMarker = false;
        _hasMarkerDragMoved = false;
        _dragMarkerIndex = -1;
    }

    private void UpdateEdgeScrollTimer(double mouseX)
    {
        double width = ActualWidth;
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
        if ((!_isDragging && !(_isDraggingMarker && _hasMarkerDragMoved))
            || ActualWidth <= 0
            || PixelsPerSecond <= 0
            || GetTotalSeconds() <= 0)
        {
            _edgeScrollTimer.Stop();
            return;
        }

        double width = ActualWidth;
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
            InvalidateBackground();
            ViewportChanged?.Invoke(this, EventArgs.Empty);
        }

        if (_isDraggingMarker)
        {
            TimeSpan previewTime = ComputeSnappedAndClampedMarkerTime(_lastDragX);
            _dragMarkerSeconds = previewTime.TotalSeconds;
            RenderSegmentsLayer();
            UpdateHoverPopup(new Point(_lastDragX, FilmstripTop), previewTime);
            BoundaryDragPreview?.Invoke(this, (_dragMarkerIndex, previewTime));
            return;
        }

        TimeSpan target = XToTime(_lastDragX);
        _dragPositionSeconds = target.TotalSeconds;
        RenderPlayheadLayer();
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
        ImageSource? image = Thumbnails?.GetThumbnail(time);
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

    private FormattedText GetOrCreateLabelText(string text, double pixelsPerDip)
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
            _textSecondaryBrush,
            pixelsPerDip);

        _labelCache[text] = ft;
        return ft;
    }

    private FormattedText GetOrCreateSegmentLabelText(string name, double segmentWidth, bool enabled, double pixelsPerDip)
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
            enabled ? _textPrimaryBrush : _textMutedBrush,
            pixelsPerDip)
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

        var oddBrush = new SolidColorBrush(Color.FromRgb(0x24, 0x46, 0x70));
        oddBrush.Freeze();
        _segmentOddBrush = oddBrush;

        _segmentDisabledBrush = ResolveBrush("DisabledBackgroundBrush", Color.FromRgb(0x18, 0x1B, 0x20));

        var scrimBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x09, 0x0A, 0x0C));
        scrimBrush.Freeze();
        _disabledFilmstripScrimBrush = scrimBrush;

        var markerBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
        markerBrush.Freeze();
        _markerBrush = markerBrush;

        var markerSelectedBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        markerSelectedBrush.Freeze();
        _markerSelectedBrush = markerSelectedBrush;

        Brush borderBrush = ResolveBrush("BorderBrush", Color.FromRgb(0x2E, 0x34, 0x40));
        Brush selectedBorderBrush = ResolveBrush("AccentHoverBrush", Color.FromRgb(0x60, 0xA5, 0xFA));

        _borderPen = CreateFrozenPen(borderBrush, 1.0);
        _majorTickPen = CreateFrozenPen(_textSecondaryBrush, 1.0);
        _minorTickPen = CreateFrozenPen(_textMutedBrush, 1.0);
        _playheadPen = CreateFrozenPen(_playheadBrush, 2.0);
        _segmentBorderPen = CreateFrozenPen(borderBrush, 1.0);
        _segmentSelectedPen = CreateFrozenPen(selectedBorderBrush, 1.5);
        _markerPen = CreateFrozenPen(_markerBrush, 2.0);
        _markerSelectedPen = CreateFrozenPen(_markerSelectedBrush, 2.5);
        _markerHandleOutlinePen = CreateFrozenPen(_backgroundBrush, 1.0);

        var hoverBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xED, 0xF0, 0xF5));
        hoverBrush.Freeze();
        _hoverPen = CreateFrozenPen(hoverBrush, 1.0);

        var diamond = new StreamGeometry();
        using (StreamGeometryContext ctx = diamond.Open())
        {
            ctx.BeginFigure(new Point(0.0, -5.0), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(5.0, 0.0), isStroked: true, isSmoothJoin: false);
            ctx.LineTo(new Point(0.0, 5.0), isStroked: true, isSmoothJoin: false);
            ctx.LineTo(new Point(-5.0, 0.0), isStroked: true, isSmoothJoin: false);
        }

        diamond.Freeze();
        _markerDiamondGeometry = diamond;

        _labelTypeface = new Typeface("Consolas");
        _emptyTypeface = new Typeface("Segoe UI");
    }

    private static Brush ResolveBrush(string resourceKey, Color fallbackColor)
    {
        if (Application.Current?.TryFindResource(resourceKey) is Brush found)
        {
            if (found.CanFreeze && !found.IsFrozen)
            {
                Brush clone = found.Clone();
                clone.Freeze();
                return clone;
            }

            return found;
        }

        var fallback = new SolidColorBrush(fallbackColor);
        fallback.Freeze();
        return fallback;
    }

    private static Pen CreateFrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
