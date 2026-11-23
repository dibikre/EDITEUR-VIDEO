using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using VideoSplitter.Services;

namespace VideoSplitter.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IAppLogger _logger;

    public PlayerViewModel Player { get; }
    public TimelineViewModel Timeline { get; }
    public EditViewModel Edit { get; }

    [ObservableProperty]
    private string _title = "VideoSplitter";

    public MainViewModel(
        PlayerViewModel player,
        TimelineViewModel timeline,
        EditViewModel edit,
        IAppLogger logger)
    {
        Player = player;
        Timeline = timeline;
        Edit = edit;
        _logger = logger;

        Edit.ConfigurePlaybackBridge(
            () => (Player.Position, Timeline.PixelsPerSecond),
            target => Player.SeekTo(target));

        Player.PropertyChanged += OnPlayerPropertyChanged;
        _logger.Info("MainViewModel initialise.");
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.MediaInfo))
        {
            Title = Player.MediaInfo is { } info
                ? $"VideoSplitter - {Path.GetFileName(info.Path)}"
                : "VideoSplitter";

            Timeline.ResetForNewMedia();

            if (Player.MediaInfo is { } openedMedia)
            {
                Edit.OnVideoOpened(openedMedia);
            }
        }
    }

    public void Dispose()
    {
        Player.PropertyChanged -= OnPlayerPropertyChanged;
    }
}
