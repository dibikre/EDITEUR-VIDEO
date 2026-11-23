using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using VideoSplitter.Services;
using VideoSplitter.ViewModels;

namespace VideoSplitter.Controls;

public partial class TimelineView : UserControl
{
    private TimelineViewModel? _subscribedViewModel;
    private bool _isSyncingScrollBar;

    public static readonly StyledProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.Register<TimelineView, TimeSpan>(nameof(Duration), TimeSpan.Zero);

    public static readonly StyledProperty<TimeSpan> PositionProperty =
        AvaloniaProperty.Register<TimelineView, TimeSpan>(nameof(Position), TimeSpan.Zero);

    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<TimelineView, bool>(nameof(IsPlaying), false);

    public static readonly StyledProperty<double> PlaybackSpeedProperty =
        AvaloniaProperty.Register<TimelineView, double>(nameof(PlaybackSpeed), 1.0);

    public static readonly StyledProperty<IThumbnailProvider?> ThumbnailsProperty =
        AvaloniaProperty.Register<TimelineView, IThumbnailProvider?>(nameof(Thumbnails), null);

    public static readonly StyledProperty<TimelineViewModel?> TimelineProperty =
        AvaloniaProperty.Register<TimelineView, TimelineViewModel?>(nameof(Timeline), null);

    public static readonly StyledProperty<EditViewModel?> EditProperty =
        AvaloniaProperty.Register<TimelineView, EditViewModel?>(nameof(Edit), null);

    static TimelineView()
    {
        DurationProperty.Changed.AddClassHandler<TimelineView>((v, _) => v.SyncScrollBar());
        TimelineProperty.Changed.AddClassHandler<TimelineView>((v, e) => v.AttachViewModel(e.NewValue as TimelineViewModel));
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

    public TimelineViewModel? Timeline
    {
        get => GetValue(TimelineProperty);
        set => SetValue(TimelineProperty, value);
    }

    public EditViewModel? Edit
    {
        get => GetValue(EditProperty);
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

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        UnsubscribeViewModel();
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

    private void OnScrollBarScroll(object? sender, ScrollEventArgs e)
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
        double width = TimelineCanvas.Bounds.Width;
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
