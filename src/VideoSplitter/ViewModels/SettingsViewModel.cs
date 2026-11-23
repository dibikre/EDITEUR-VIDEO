using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoSplitter.Models;
using VideoSplitter.Services;

namespace VideoSplitter.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IAppLogger _logger;
    private bool _isInitializing = true;
    private bool _isAdjustingFolderMode;

    public Func<string?, Task<string?>>? PickFolderAsync { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFastMode))]
    [NotifyPropertyChangedFor(nameof(IsPreciseMode))]
    [NotifyPropertyChangedFor(nameof(ExportSummaryText))]
    private ExportMode _exportMode = ExportMode.Fast;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSameAsSourceFolder))]
    [NotifyPropertyChangedFor(nameof(IsCustomOutputFolder))]
    [NotifyPropertyChangedFor(nameof(IsOverwriteSource))]
    [NotifyPropertyChangedFor(nameof(CustomFolderDisplay))]
    [NotifyPropertyChangedFor(nameof(ExportSummaryText))]
    private OutputFolderMode _outputFolderMode = OutputFolderMode.SameAsSource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CustomFolderDisplay))]
    [NotifyPropertyChangedFor(nameof(ExportSummaryText))]
    private string _customOutputFolder = string.Empty;

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

        AppSettings loaded = _settingsService.Load();
        _exportMode = loaded.ExportMode;
        _outputFolderMode = loaded.OutputFolderMode;
        _customOutputFolder = loaded.CustomOutputFolder;
        _showVolumeGauge = loaded.ShowVolumeGauge;
        _isInitializing = false;
    }

    partial void OnExportModeChanged(ExportMode value) => PersistIfReady();

    partial void OnOutputFolderModeChanged(OutputFolderMode value) => PersistIfReady();

    partial void OnCustomOutputFolderChanged(string value) => PersistIfReady();

    partial void OnShowVolumeGaugeChanged(bool value) => PersistIfReady();

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

    private void PersistIfReady()
    {
        if (_isInitializing)
        {
            return;
        }

        _settingsService.Save(new AppSettings
        {
            ExportMode = ExportMode,
            OutputFolderMode = OutputFolderMode,
            CustomOutputFolder = CustomOutputFolder,
            ShowVolumeGauge = ShowVolumeGauge
        });
    }
}
