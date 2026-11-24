using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoSplitter.Export;
using VideoSplitter.Models;
using VideoSplitter.Services;

namespace VideoSplitter.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly IAppLogger _logger;
    private readonly DispatcherTimer _saveDebounceTimer;
    private bool _isInitializing = true;
    private bool _isAdjustingFolderMode;
    private bool _hasPendingSave;
    private bool _disposed;
    private string _sampleSourceName = "video";
    private string _sampleSourceExtension = ".mp4";

    public int Crf { get; private set; } = 18;
    public EncoderSelection SelectedEncoder { get; private set; } = EncoderSelection.Auto;
    public bool SnapToKeyframes { get; private set; } = true;
    public bool FollowPlayback { get; private set; } = true;
    public int Volume { get; private set; } = 100;
    public bool IsMuted { get; private set; }
    public double PlaybackSpeed { get; private set; } = 1.0;

    public Func<string?, Task<string?>>? PickFolderAsync { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFastMode))]
    [NotifyPropertyChangedFor(nameof(IsPreciseMode))]
    [NotifyPropertyChangedFor(nameof(FileNamePreviewText))]
    [NotifyPropertyChangedFor(nameof(ExportSummaryText))]
    private ExportMode _exportMode = ExportMode.Fast;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSameAsSourceFolder))]
    [NotifyPropertyChangedFor(nameof(IsCustomOutputFolder))]
    [NotifyPropertyChangedFor(nameof(IsOverwriteSource))]
    [NotifyPropertyChangedFor(nameof(CustomFolderDisplay))]
    [NotifyPropertyChangedFor(nameof(FileNamePreviewText))]
    [NotifyPropertyChangedFor(nameof(ExportSummaryText))]
    private OutputFolderMode _outputFolderMode = OutputFolderMode.SameAsSource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CustomFolderDisplay))]
    [NotifyPropertyChangedFor(nameof(ExportSummaryText))]
    private string _customOutputFolder = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileNamePreviewText))]
    private string _fileNamePattern = ExportPlanner.DefaultPattern;

    [ObservableProperty]
    private bool _showVolumeGauge = true;

    public bool IsFastMode
    {
        get => ExportMode == ExportMode.Fast;
        set
        {
            if (value)
            {
                ExportMode = ExportMode.Fast;
            }
        }
    }

    public bool IsPreciseMode
    {
        get => ExportMode == ExportMode.Precise;
        set
        {
            if (value)
            {
                ExportMode = ExportMode.Precise;
            }
        }
    }

    public bool IsSameAsSourceFolder
    {
        get => OutputFolderMode == OutputFolderMode.SameAsSource;
        set
        {
            if (value)
            {
                OutputFolderMode = OutputFolderMode.SameAsSource;
            }
        }
    }

    public bool IsCustomOutputFolder
    {
        get => OutputFolderMode == OutputFolderMode.CustomFolder;
        set
        {
            if (value)
            {
                _ = SelectCustomFolderModeAsync();
            }
        }
    }

    public bool IsOverwriteSource
    {
        get => OutputFolderMode == OutputFolderMode.OverwriteSource;
        set
        {
            if (value)
            {
                OutputFolderMode = OutputFolderMode.OverwriteSource;
            }
        }
    }

    public string CustomFolderDisplay => string.IsNullOrWhiteSpace(CustomOutputFolder)
        ? "Aucun dossier sélectionné"
        : CustomOutputFolder;

    public string FileNamePreviewText
    {
        get
        {
            string ext = ExportPlanner.ResolveOutputExtension(_sampleSourceExtension, ExportMode);
            if (OutputFolderMode == OutputFolderMode.OverwriteSource)
            {
                return $"Remplacement de la source : {_sampleSourceName}{ext}";
            }

            var sampleSegment = new Segment(Guid.Empty, TimeSpan.Zero, TimeSpan.FromSeconds(10), true, "Segment 1");
            string stem = ExportPlanner.BuildFileName(FileNamePattern, _sampleSourceName, 1, 2, sampleSegment);
            return $"Exemple : {stem}{ext}";
        }
    }

    public string ExportSummaryText
    {
        get
        {
            string modeLabel = ExportMode == ExportMode.Fast ? "Mode Rapide" : "Mode Précis";
            string folderLabel = OutputFolderMode switch
            {
                OutputFolderMode.CustomFolder when !string.IsNullOrWhiteSpace(CustomOutputFolder) => CustomOutputFolder,
                OutputFolderMode.OverwriteSource => "Écraser la vidéo source",
                _ => "Même dossier que la source"
            };
            return $"{modeLabel} · Sortie : {folderLabel}";
        }
    }

    public SettingsViewModel(ISettingsService settingsService, IAppLogger logger)
    {
        _settingsService = settingsService;
        _logger = logger;

        _saveDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _saveDebounceTimer.Tick += OnSaveDebounceTick;

        AppSettings loaded = _settingsService.Load();
        _exportMode = loaded.ExportMode;
        _outputFolderMode = loaded.OutputFolderMode;
        _customOutputFolder = loaded.CustomOutputFolder;
        _fileNamePattern = loaded.FileNamePattern;
        _showVolumeGauge = loaded.ShowVolumeGauge;
        Crf = loaded.Crf;
        SelectedEncoder = loaded.SelectedEncoder;
        SnapToKeyframes = loaded.SnapToKeyframes;
        FollowPlayback = loaded.FollowPlayback;
        Volume = loaded.Volume;
        IsMuted = loaded.IsMuted;
        PlaybackSpeed = loaded.PlaybackSpeed;
        _isInitializing = false;
    }

    public void UpdateMediaSample(string? mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            _sampleSourceName = "video";
            _sampleSourceExtension = ".mp4";
        }
        else
        {
            string name = Path.GetFileNameWithoutExtension(mediaPath);
            string ext = Path.GetExtension(mediaPath);
            _sampleSourceName = string.IsNullOrWhiteSpace(name) ? "video" : name;
            _sampleSourceExtension = string.IsNullOrWhiteSpace(ext) ? ".mp4" : ext;
        }

        OnPropertyChanged(nameof(FileNamePreviewText));
    }

    partial void OnExportModeChanged(ExportMode value) => PersistImmediateIfReady();

    partial void OnOutputFolderModeChanged(OutputFolderMode value) => PersistImmediateIfReady();

    partial void OnCustomOutputFolderChanged(string value) => PersistImmediateIfReady();

    partial void OnFileNamePatternChanged(string value) => ScheduleSaveIfReady();

    partial void OnShowVolumeGaugeChanged(bool value) => PersistImmediateIfReady();

    public void UpdateExportTuning(int crf, EncoderSelection encoder)
    {
        int clampedCrf = Math.Clamp(crf, 14, 28);
        bool encoderChanged = SelectedEncoder != encoder;
        bool crfChanged = Crf != clampedCrf;
        if (!encoderChanged && !crfChanged)
        {
            return;
        }

        Crf = clampedCrf;
        SelectedEncoder = encoder;
        if (encoderChanged)
        {
            PersistImmediateIfReady();
        }
        else
        {
            ScheduleSaveIfReady();
        }
    }

    public void UpdateSnapToKeyframes(bool snap)
    {
        if (SnapToKeyframes == snap)
        {
            return;
        }

        SnapToKeyframes = snap;
        PersistImmediateIfReady();
    }

    public void UpdateFollowPlayback(bool follow)
    {
        if (FollowPlayback == follow)
        {
            return;
        }

        FollowPlayback = follow;
        PersistImmediateIfReady();
    }

    public void UpdatePlayerPreferences(int volume, bool isMuted, double speed)
    {
        int clampedVolume = Math.Clamp(volume, 0, 100);
        bool discreteChanged = IsMuted != isMuted || Math.Abs(PlaybackSpeed - speed) > 0.001;
        bool volumeChanged = Volume != clampedVolume;
        if (!discreteChanged && !volumeChanged)
        {
            return;
        }

        Volume = clampedVolume;
        IsMuted = isMuted;
        PlaybackSpeed = speed;

        if (discreteChanged)
        {
            PersistImmediateIfReady();
        }
        else
        {
            ScheduleSaveIfReady();
        }
    }

    public void SaveNow()
    {
        if (_isInitializing || _disposed)
        {
            return;
        }

        _saveDebounceTimer.Stop();
        _hasPendingSave = false;
        _settingsService.Save(BuildSnapshot());
    }

    [RelayCommand]
    private async Task BrowseCustomFolderAsync()
    {
        if (PickFolderAsync is null)
        {
            return;
        }

        string? initialDir = !string.IsNullOrWhiteSpace(CustomOutputFolder)
            ? CustomOutputFolder
            : null;

        string? selected = await PickFolderAsync.Invoke(initialDir);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            CustomOutputFolder = selected;
            OutputFolderMode = OutputFolderMode.CustomFolder;
            _logger.Info($"Dossier d'export personnalise defini : {selected}");
        }
    }

    private async Task SelectCustomFolderModeAsync()
    {
        if (_isAdjustingFolderMode)
        {
            return;
        }

        _isAdjustingFolderMode = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(CustomOutputFolder))
            {
                OutputFolderMode = OutputFolderMode.CustomFolder;
                return;
            }

            if (PickFolderAsync is null)
            {
                OutputFolderMode = OutputFolderMode.SameAsSource;
                return;
            }

            string? selected = await PickFolderAsync.Invoke(null);
            if (!string.IsNullOrWhiteSpace(selected))
            {
                CustomOutputFolder = selected;
                OutputFolderMode = OutputFolderMode.CustomFolder;
                _logger.Info($"Dossier d'export personnalise defini : {selected}");
            }
            else
            {
                OutputFolderMode = OutputFolderMode.SameAsSource;
                OnPropertyChanged(nameof(IsSameAsSourceFolder));
                OnPropertyChanged(nameof(IsCustomOutputFolder));
                OnPropertyChanged(nameof(IsOverwriteSource));
            }
        }
        finally
        {
            _isAdjustingFolderMode = false;
        }
    }

    private void PersistImmediateIfReady()
    {
        if (_isInitializing || _disposed)
        {
            return;
        }

        _saveDebounceTimer.Stop();
        _hasPendingSave = false;
        _settingsService.Save(BuildSnapshot());
    }

    private void ScheduleSaveIfReady()
    {
        if (_isInitializing || _disposed)
        {
            return;
        }

        _hasPendingSave = true;
        _saveDebounceTimer.Stop();
        _saveDebounceTimer.Start();
    }

    private void OnSaveDebounceTick(object? sender, EventArgs e)
    {
        _saveDebounceTimer.Stop();
        if (_hasPendingSave && !_isInitializing && !_disposed)
        {
            _hasPendingSave = false;
            _settingsService.Save(BuildSnapshot());
        }
    }

    private AppSettings BuildSnapshot() => new()
    {
        ExportMode = ExportMode,
        OutputFolderMode = OutputFolderMode,
        CustomOutputFolder = CustomOutputFolder,
        FileNamePattern = FileNamePattern,
        ShowVolumeGauge = ShowVolumeGauge,
        Crf = Crf,
        SelectedEncoder = SelectedEncoder,
        SnapToKeyframes = SnapToKeyframes,
        FollowPlayback = FollowPlayback,
        Volume = Volume,
        IsMuted = IsMuted,
        PlaybackSpeed = PlaybackSpeed
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_hasPendingSave && !_isInitializing)
        {
            _hasPendingSave = false;
            _settingsService.Save(BuildSnapshot());
        }

        _disposed = true;
        _saveDebounceTimer.Stop();
        _saveDebounceTimer.Tick -= OnSaveDebounceTick;
    }
}
