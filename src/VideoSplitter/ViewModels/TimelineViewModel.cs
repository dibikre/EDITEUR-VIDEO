using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VideoSplitter.ViewModels;

public sealed partial class TimelineViewModel : ObservableObject
{
    public const double MaxPixelsPerSecond = 500.0;
    public const double ButtonZoomFactor = 1.5;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomLabel))]
    private double _pixelsPerSecond = 10.0;

    [ObservableProperty]
    private double _scrollOffsetSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomLabel))]
    private bool _isFitToWindow = true;

    [ObservableProperty]
    private bool _followPlayback = true;

    public string ZoomLabel => IsFitToWindow
        ? $"Ajuste ({FormatZoomRate(PixelsPerSecond)})"
        : FormatZoomRate(PixelsPerSecond);

    public event EventHandler<double>? ZoomStepRequested;

    partial void OnPixelsPerSecondChanged(double value)
    {
        double clamped = Math.Clamp(value, 0.001, MaxPixelsPerSecond);
        if (Math.Abs(clamped - value) > 0.0001)
        {
            PixelsPerSecond = clamped;
        }
    }

    partial void OnScrollOffsetSecondsChanged(double value)
    {
        if (value < 0.0 || double.IsNaN(value))
        {
            ScrollOffsetSeconds = 0.0;
        }
    }

    [RelayCommand]
    private void ZoomIn()
    {
        IsFitToWindow = false;
        ZoomStepRequested?.Invoke(this, ButtonZoomFactor);
    }

    [RelayCommand]
    private void ZoomOut()
    {
        IsFitToWindow = false;
        ZoomStepRequested?.Invoke(this, 1.0 / ButtonZoomFactor);
    }

    [RelayCommand]
    private void FitToWindow()
    {
        IsFitToWindow = true;
    }

    public void ResetForNewMedia()
    {
        ScrollOffsetSeconds = 0.0;
        IsFitToWindow = true;
    }

    private static string FormatZoomRate(double pps)
        => pps >= 10.0 ? $"{pps:F0} px/s" : $"{pps:F1} px/s";
}
