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
        Export.ConfigureOutputLocation(Settings.OutputFolderMode, Settings.CustomOutputFolder);

        Edit.ConfigurePlaybackBridge(
            () => (Player.Position, Timeline.PixelsPerSecond),
            target => Player.SeekTo(target));

        Player.BeforeOpenConfirmAsync = ConfirmAndAbortExportBeforeOpenAsync;
        Player.PropertyChanged += OnPlayerPropertyChanged;
        Edit.PropertyChanged += OnEditPropertyChanged;
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

            if (Player.MediaInfo is { } openedMedia)
            {
                Edit.OnVideoOpened(openedMedia);
                Export.OnVideoOpened(openedMedia);
            }
        }
    }

    private void OnEditPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditViewModel.Segments))
        {
            Export.NotifySegmentsChanged();
            Export.ClearTransientErrorState();
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.ExportMode))
        {
            Export.Mode = Settings.ExportMode;
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
        Settings.PropertyChanged -= OnSettingsPropertyChanged;
        Player.BeforeOpenConfirmAsync = null;
    }
}
