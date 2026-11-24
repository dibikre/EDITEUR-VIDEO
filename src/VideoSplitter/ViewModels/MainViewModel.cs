using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using VideoSplitter.Services;

namespace VideoSplitter.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IAppLogger _logger;
    private bool _isReloadingAfterOverwrite;

    public PlayerViewModel Player { get; }
    public TimelineViewModel Timeline { get; }
    public EditViewModel Edit { get; }
    public ExportViewModel Export { get; }
    public SettingsViewModel Settings { get; }

    public Func<Task<bool>>? ConfirmAbortExportForOpenAsync { get; set; }

    [ObservableProperty]
    private string _title = "VideoSplitter";

    public MainViewModel(
        PlayerViewModel player,
        TimelineViewModel timeline,
        EditViewModel edit,
        ExportViewModel export,
        SettingsViewModel settings,
        IAppLogger logger)
    {
        Player = player;
        Timeline = timeline;
        Edit = edit;
        Export = export;
        Settings = settings;
        _logger = logger;

        Export.Mode = Settings.ExportMode;
        Export.Crf = Settings.Crf;
        Export.SetInitialEncoder(Settings.SelectedEncoder);
        Export.FileNamePattern = Settings.FileNamePattern;
        Export.ConfigureOutputLocation(Settings.OutputFolderMode, Settings.CustomOutputFolder);
        Export.ConfigureOverwriteHooks(
            prepareOverwriteReleaseAsync: () => Player.ReleaseMediaForOverwriteAsync(),
            reloadAfterOverwriteAsync: async path =>
            {
                _isReloadingAfterOverwrite = true;
                try
                {
                    await Player.ReloadAfterOverwriteAsync(path);
                }
                finally
                {
                    _isReloadingAfterOverwrite = false;
                }
            });

        Edit.SnapToKeyframes = Settings.SnapToKeyframes;
        Timeline.FollowPlayback = Settings.FollowPlayback;
        Player.Volume = Settings.Volume;
        Player.IsMuted = Settings.IsMuted;
        Player.Speed = Settings.PlaybackSpeed;

        Edit.ConfigurePlaybackBridge(
            () => (Player.Position, Timeline.PixelsPerSecond),
            target => Player.SeekTo(target));

        Player.BeforeOpenConfirmAsync = ConfirmAndAbortExportBeforeOpenAsync;
        Player.PropertyChanged += OnPlayerPropertyChanged;
        Edit.PropertyChanged += OnEditPropertyChanged;
        Timeline.PropertyChanged += OnTimelinePropertyChanged;
        Export.PropertyChanged += OnExportPropertyChanged;
        Settings.PropertyChanged += OnSettingsPropertyChanged;

        _logger.Info("MainViewModel initialise.");
    }

    private async Task<bool> ConfirmAndAbortExportBeforeOpenAsync()
    {
        if (!Export.IsExporting)
        {
            return true;
        }

        if (ConfirmAbortExportForOpenAsync is null || !await ConfirmAbortExportForOpenAsync.Invoke())
        {
            return false;
        }

        await Export.CancelAndWaitAsync(TimeSpan.FromSeconds(5));
        return true;
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.MediaInfo))
        {
            Title = Player.MediaInfo is { } info
                ? $"VideoSplitter - {Path.GetFileName(info.Path)}"
                : "VideoSplitter";

            Timeline.ResetForNewMedia();

            Settings.UpdateMediaSample(Player.MediaInfo?.Path);

            if (Player.MediaInfo is { } openedMedia)
            {
                Edit.OnVideoOpened(openedMedia);
                if (_isReloadingAfterOverwrite)
                {
                    Export.UpdateCurrentMediaWithoutReset(openedMedia);
                }
                else
                {
                    Export.OnVideoOpened(openedMedia);
                }
            }
        }
        else if (e.PropertyName is nameof(PlayerViewModel.Volume) or nameof(PlayerViewModel.IsMuted) or nameof(PlayerViewModel.Speed))
        {
            Settings.UpdatePlayerPreferences(Player.Volume, Player.IsMuted, Player.Speed);
        }
    }

    private void OnEditPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditViewModel.Segments))
        {
            Export.NotifySegmentsChanged();
            if (!_isReloadingAfterOverwrite)
            {
                Export.ClearTransientErrorState();
            }
        }
        else if (e.PropertyName == nameof(EditViewModel.SnapToKeyframes))
        {
            Settings.UpdateSnapToKeyframes(Edit.SnapToKeyframes);
        }
    }

    private void OnTimelinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TimelineViewModel.FollowPlayback))
        {
            Settings.UpdateFollowPlayback(Timeline.FollowPlayback);
        }
    }

    private void OnExportPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ExportViewModel.Crf) or nameof(ExportViewModel.SelectedEncoder))
        {
            Settings.UpdateExportTuning(Export.Crf, Export.SelectedEncoder);
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.ExportMode))
        {
            Export.Mode = Settings.ExportMode;
        }
        else if (e.PropertyName == nameof(SettingsViewModel.FileNamePattern))
        {
            Export.FileNamePattern = Settings.FileNamePattern;
        }
        else if (e.PropertyName is nameof(SettingsViewModel.OutputFolderMode) or nameof(SettingsViewModel.CustomOutputFolder))
        {
            Export.ConfigureOutputLocation(Settings.OutputFolderMode, Settings.CustomOutputFolder);
        }
    }

    public void Dispose()
    {
        Player.PropertyChanged -= OnPlayerPropertyChanged;
        Edit.PropertyChanged -= OnEditPropertyChanged;
        Timeline.PropertyChanged -= OnTimelinePropertyChanged;
        Export.PropertyChanged -= OnExportPropertyChanged;
        Settings.PropertyChanged -= OnSettingsPropertyChanged;
        Player.BeforeOpenConfirmAsync = null;
    }
}
