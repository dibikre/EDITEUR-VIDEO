using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using VideoSplitter.Services;
using VideoSplitter.ViewModels;

namespace VideoSplitter.Controls;

public partial class TimelineView : UserControl
{
    private TimelineViewModel? _subscribedViewModel;
    private bool _isSyncingScrollBar;

    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(
        nameof(Duration),
        typeof(TimeSpan),
        typeof(TimelineView),
        new PropertyMetadata(TimeSpan.Zero, OnViewportDependencyChanged));

    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position),
        typeof(TimeSpan),
        typeof(TimelineView),
        new PropertyMetadata(TimeSpan.Zero));

    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(
        nameof(IsPlaying),
        typeof(bool),
        typeof(TimelineView),
        new PropertyMetadata(false));

    public static readonly DependencyProperty PlaybackSpeedProperty = DependencyProperty.Register(
        nameof(PlaybackSpeed),
        typeof(double),
        typeof(TimelineView),
        new PropertyMetadata(1.0));

    public static readonly DependencyProperty ThumbnailsProperty = DependencyProperty.Register(
        nameof(Thumbnails),
        typeof(IThumbnailProvider),
        typeof(TimelineView),
        new PropertyMetadata(null));

    public static readonly DependencyProperty TimelineProperty = DependencyProperty.Register(
        nameof(Timeline),
        typeof(TimelineViewModel),
        typeof(TimelineView),
        new PropertyMetadata(null, OnTimelineViewModelChanged));

    public static readonly DependencyProperty EditProperty = DependencyProperty.Register(
        nameof(Edit),
        typeof(EditViewModel),
        typeof(TimelineView),
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

    public TimelineViewModel? Timeline
    {
        get => (TimelineViewModel?)GetValue(TimelineProperty);
        set => SetValue(TimelineProperty, value);
    }

    public EditViewModel? Edit
    {
        get => (EditViewModel?)GetValue(EditProperty);
        set => SetValue(EditProperty, value);
    }

    public event EventHandler<TimeSpan>? SeekRequested;
    public event EventHandler<TimeSpan>? SeekCompleted;
    public event EventHandler<Guid?>? SegmentSelectionRequested;
    public event EventHandler<int?>? MarkerSelectionRequested;
    public event EventHandler<(int Index, TimeSpan Time)>? BoundaryDragPreview;
    public event EventHandler<(int Index, TimeSpan Time, bool Cancelled)>? BoundaryDragCompleted;

    public TimelineView()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    public bool CancelActiveDrag() => TimelineCanvas.CancelActiveDrag();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeViewModel();
    }

    private static void OnViewportDependencyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimelineView view)
        {
            view.SyncScrollBar();
        }
    }

    private static void OnTimelineViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimelineView view)
        {
            view.AttachViewModel(e.NewValue as TimelineViewModel);
        }
    }

    private void AttachViewModel(TimelineViewModel? vm)
    {
        UnsubscribeViewModel();
        _subscribedViewModel = vm;
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.ZoomStepRequested += OnZoomStepRequested;
        }
    }

    private void UnsubscribeViewModel()
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.ZoomStepRequested -= OnZoomStepRequested;
            _subscribedViewModel = null;
        }
    }

    private void OnZoomStepRequested(object? sender, double factor)
    {
        TimelineCanvas.ZoomByFactorCentered(factor);
    }

    private void OnTimelineSeekRequested(object? sender, TimeSpan target)
    {
        SeekRequested?.Invoke(this, target);
    }

    private void OnTimelineSeekCompleted(object? sender, TimeSpan target)
    {
        SeekCompleted?.Invoke(this, target);
    }

    private void OnTimelineSegmentSelectionRequested(object? sender, Guid? segmentId)
    {
        SegmentSelectionRequested?.Invoke(this, segmentId);
    }

    private void OnTimelineMarkerSelectionRequested(object? sender, int? markerIndex)
    {
        MarkerSelectionRequested?.Invoke(this, markerIndex);
    }

    private void OnTimelineBoundaryDragPreview(object? sender, (int Index, TimeSpan Time) e)
    {
        BoundaryDragPreview?.Invoke(this, e);
    }

    private void OnTimelineBoundaryDragCompleted(object? sender, (int Index, TimeSpan Time, bool Cancelled) e)
    {
        BoundaryDragCompleted?.Invoke(this, e);
    }

    private void OnTimelineViewportChanged(object? sender, EventArgs e)
    {
        SyncScrollBar();
    }

    private void OnScrollBarScroll(object sender, ScrollEventArgs e)
    {
        if (_isSyncingScrollBar)
        {
            return;
        }

        TimelineCanvas.NotifyUserNavigated();
        TimelineCanvas.ScrollOffsetSeconds = HorizontalScrollBar.Value;
    }

    private void SyncScrollBar()
    {
        double totalSeconds = Duration > TimeSpan.Zero ? Duration.TotalSeconds : 0.0;
        double width = TimelineCanvas.ActualWidth;
        double pps = TimelineCanvas.PixelsPerSecond;

        _isSyncingScrollBar = true;
        try
        {
            if (totalSeconds <= 0 || width <= 0 || pps <= 0)
            {
                HorizontalScrollBar.Minimum = 0.0;
                HorizontalScrollBar.Maximum = 0.0;
                HorizontalScrollBar.ViewportSize = 1.0;
                HorizontalScrollBar.Value = 0.0;
                HorizontalScrollBar.IsEnabled = false;
                return;
            }

            double visibleSeconds = width / pps;
            double maxOffset = Math.Max(0.0, totalSeconds - visibleSeconds);

            HorizontalScrollBar.Minimum = 0.0;
            HorizontalScrollBar.Maximum = maxOffset;
            HorizontalScrollBar.ViewportSize = Math.Min(totalSeconds, visibleSeconds);
            HorizontalScrollBar.SmallChange = Math.Max(0.05, visibleSeconds * 0.05);
            HorizontalScrollBar.LargeChange = Math.Max(0.1, visibleSeconds * 0.5);
            HorizontalScrollBar.Value = Math.Clamp(TimelineCanvas.ScrollOffsetSeconds, 0.0, maxOffset);
            HorizontalScrollBar.IsEnabled = maxOffset > 0.001;
        }
        finally
        {
            _isSyncingScrollBar = false;
        }
    }
}
