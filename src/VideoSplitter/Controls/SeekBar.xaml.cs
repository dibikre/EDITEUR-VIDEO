using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace VideoSplitter.Controls;

public partial class SeekBar : UserControl
{
    private const long SeekThrottleMilliseconds = 50;

    private bool _isUserDragging;
    private bool _isUpdatingFromProperty;
    private long _lastSeekTimestampMs;

    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(
        nameof(Duration),
        typeof(TimeSpan),
        typeof(SeekBar),
        new PropertyMetadata(TimeSpan.Zero, OnDurationChanged));

    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position),
        typeof(TimeSpan),
        typeof(SeekBar),
        new PropertyMetadata(TimeSpan.Zero, OnPositionChanged));

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

    public event EventHandler<TimeSpan>? SeekRequested;

    public SeekBar()
    {
        InitializeComponent();

        ProgressSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnThumbDragStarted), true);
        ProgressSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnThumbDragCompleted), true);
    }

    private static void OnDurationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SeekBar seekBar)
        {
            seekBar.SyncSliderBounds();
        }
    }

    private static void OnPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SeekBar seekBar && !seekBar._isUserDragging)
        {
            seekBar.SyncSliderPosition();
        }
    }

    private void SyncSliderBounds()
    {
        _isUpdatingFromProperty = true;
        try
        {
            double maxSeconds = Duration > TimeSpan.Zero ? Duration.TotalSeconds : 1.0;
            ProgressSlider.Maximum = maxSeconds;
            if (ProgressSlider.Value > maxSeconds)
            {
                ProgressSlider.Value = maxSeconds;
            }
        }
        finally
        {
            _isUpdatingFromProperty = false;
        }
    }

    private void SyncSliderPosition()
    {
        _isUpdatingFromProperty = true;
        try
        {
            double target = Math.Clamp(Position.TotalSeconds, 0.0, ProgressSlider.Maximum);
            ProgressSlider.Value = target;
        }
        finally
        {
            _isUpdatingFromProperty = false;
        }
    }

    private void OnThumbDragStarted(object sender, DragStartedEventArgs e)
    {
        _isUserDragging = true;
    }

    private void OnThumbDragCompleted(object sender, DragCompletedEventArgs e)
    {
        CompleteUserDrag();
    }

    private void OnSliderPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isUserDragging = true;
    }

    private void OnSliderPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isUserDragging)
        {
            CompleteUserDrag();
        }
    }

    private void CompleteUserDrag()
    {
        _isUserDragging = false;
        EmitSeekRequested(force: true);
    }

    private void OnSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingFromProperty || !_isUserDragging)
        {
            return;
        }

        EmitSeekRequested(force: false);
    }

    private void EmitSeekRequested(bool force)
    {
        if (Duration <= TimeSpan.Zero)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (!force && now - _lastSeekTimestampMs < SeekThrottleMilliseconds)
        {
            return;
        }

        _lastSeekTimestampMs = now;
        TimeSpan target = TimeSpan.FromSeconds(Math.Clamp(ProgressSlider.Value, 0.0, Duration.TotalSeconds));
        SeekRequested?.Invoke(this, target);
    }
}
