using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using VideoSplitter.Services;

namespace VideoSplitter.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IAppLogger _logger;

    public PlayerViewModel Player { get; }

    [ObservableProperty]
    private string _title = "VideoSplitter";

    public MainViewModel(PlayerViewModel player, IAppLogger logger)
    {
        Player = player;
        _logger = logger;

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
        }
    }

    public void Dispose()
    {
        Player.PropertyChanged -= OnPlayerPropertyChanged;
    }
}
