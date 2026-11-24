using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoSplitter.Export;
using VideoSplitter.Models;
using VideoSplitter.Services;
using VideoSplitter.Views;

namespace VideoSplitter.ViewModels;

public enum AppTaskbarState
{
    None,
    Normal,
    Error
}

public sealed partial class EncoderOptionItem : ObservableObject
{
    public EncoderSelection Kind { get; }
    public string Label { get; }

    [ObservableProperty]
    private bool _isEnabled;

    public EncoderOptionItem(EncoderSelection kind, string label, bool isEnabled)
    {
        Kind = kind;
        Label = label;
        _isEnabled = isEnabled;
    }
}

public sealed partial class ExportViewModel : ObservableObject, IDisposable
{
    [Flags]
    private enum ExecutionState : uint
    {
        EsContinuous = 0x80000000,
        EsSystemRequired = 0x00000001
    }

    private const uint MbIconAsterisk = 0x00000040;

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern uint SetThreadExecutionState(ExecutionState esFlags);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint uType);

    private readonly IFfmpegService _ffmpegService;
    private readonly IExportQueue _exportQueue;
    private readonly IAppLogger _logger;
    private readonly List<string> _lastExportedFiles = [];

    private Func<(MediaInfo? Media, IReadOnlyList<Segment> Segments, IReadOnlyList<double> Keyframes)>? _sourceProvider;
    private Func<int, bool, bool, Task<FastCutWarningChoice>>? _fastCutWarningPrompt;
    private Func<IReadOnlyList<string>, Task<ConflictPolicy?>>? _conflictPrompt;
    private Func<string, Task<bool>>? _confirmContinuePrompt;
    private Func<string, Task>? _errorPrompt;
    private Func<string, Task>? _copyTextAction;
    private Func<Task>? _prepareOverwriteReleaseAsync;
    private Func<string, Task>? _reloadAfterOverwriteAsync;

    private CancellationTokenSource? _encoderDetectCts;
    private CancellationTokenSource? _exportCts;
    private Task? _activeExportTask;
    private EncoderCapabilities _capabilities = new(new HashSet<EncoderKind> { EncoderKind.Cpu });
    private MediaInfo? _currentMedia;
    private OutputFolderMode _outputFolderMode = OutputFolderMode.SameAsSource;
    private string _customOutputFolder = string.Empty;
    private EncoderSelection? _preferredEncoderToRestore;
    private bool _hasEnabledSegments;
    private bool _disposed;

    public ObservableCollection<ExportItemViewModel> Items { get; } = [];

    public IReadOnlyList<EncoderOptionItem> EncoderOptions { get; } =
    [
        new(EncoderSelection.Auto, "Auto", true),
        new(EncoderSelection.Cpu, "CPU (libx264)", true),
        new(EncoderSelection.Nvenc, "NVENC", false),
        new(EncoderSelection.Qsv, "QSV", false),
        new(EncoderSelection.Amf, "AMF", false)
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreciseMode))]
    [NotifyPropertyChangedFor(nameof(PreviewFileName))]
    private ExportMode _mode = ExportMode.Fast;

    public bool IsPreciseMode => Mode == ExportMode.Precise;

    [ObservableProperty]
    private int _crf = 18;

    [ObservableProperty]
    private EncoderSelection _selectedEncoder = EncoderSelection.Auto;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewFileName))]
    [NotifyCanExecuteChangedFor(nameof(OpenOutputFolderCommand))]
    private string _outputFolder = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewFileName))]
    private string _fileNamePattern = ExportPlanner.DefaultPattern;

    [ObservableProperty]
    private string _encoderStatus = "Encodeurs : détection…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskbarProgressState))]
    [NotifyPropertyChangedFor(nameof(CanStartExport))]
    [NotifyCanExecuteChangedFor(
        nameof(ExportSeparateSegmentsCommand),
        nameof(ExportSingleVideoCommand),
        nameof(CancelCommand),
        nameof(OpenOutputFolderCommand))]
    private bool _isExporting;

    public bool CanStartExport => !IsExporting && _currentMedia is not null && _hasEnabledSegments;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GlobalProgressPercentText))]
    private double _globalProgress;

    public string GlobalProgressPercentText => $"{(int)Math.Round(GlobalProgress * 100.0)} %";

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _estimatedRemaining = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskbarProgressState))]
    private bool _hasExportErrors;

    public AppTaskbarState TaskbarProgressState =>
        IsExporting
            ? AppTaskbarState.Normal
            : (HasExportErrors ? AppTaskbarState.Error : AppTaskbarState.None);

    public string PreviewFileName
    {
        get
        {
            (MediaInfo? media, IReadOnlyList<Segment> segments, _) = GetSourceSnapshot();
            string sourceName = media is not null
                ? Path.GetFileNameWithoutExtension(media.Path)
                : "video";
            string sourceExt = media is not null
                ? Path.GetExtension(media.Path)
                : ".mp4";

            Segment sampleSegment = new(Guid.Empty, TimeSpan.Zero, TimeSpan.FromSeconds(10), true, "Segment 1");
            int enabledCount = 0;
            for (int i = 0; i < segments.Count; i++)
            {
                if (segments[i].Enabled)
                {
                    if (enabledCount == 0)
                    {
                        sampleSegment = segments[i];
                    }

                    enabledCount++;
                }
            }

            if (enabledCount == 0 && segments.Count > 0)
            {
                sampleSegment = segments[0];
            }

            if (_outputFolderMode == OutputFolderMode.OverwriteSource)
            {
                string effectiveExt = ExportPlanner.ResolveOutputExtension(sourceExt, Mode);
                string targetFileName = string.Equals(effectiveExt, sourceExt, StringComparison.OrdinalIgnoreCase)
                    ? (media is not null ? Path.GetFileName(media.Path) : "video.mp4")
                    : $"{sourceName}{effectiveExt}";
                return $"Remplacement de la source : {targetFileName}";
            }

            string stem = ExportPlanner.BuildFileName(
                FileNamePattern,
                sourceName,
                1,
                Math.Max(1, enabledCount),
                sampleSegment);

            string ext = ExportPlanner.ResolveOutputExtension(sourceExt, Mode);
            return $"Exemple : {stem}{ext}";
        }
    }

    public ExportViewModel(
        IFfmpegService ffmpegService,
        IExportQueue exportQueue,
        IAppLogger logger)
    {
        _ffmpegService = ffmpegService;
        _exportQueue = exportQueue;
        _logger = logger;
    }

    public void ConfigureBridges(
        Func<(MediaInfo? Media, IReadOnlyList<Segment> Segments, IReadOnlyList<double> Keyframes)> sourceProvider,
        Func<int, bool, bool, Task<FastCutWarningChoice>> fastCutWarningPrompt,
        Func<IReadOnlyList<string>, Task<ConflictPolicy?>> conflictPrompt,
        Func<string, Task<bool>> confirmContinuePrompt,
        Func<string, Task> errorPrompt,
        Func<string, Task> copyTextAction)
    {
        _sourceProvider = sourceProvider;
        _fastCutWarningPrompt = fastCutWarningPrompt;
        _conflictPrompt = conflictPrompt;
        _confirmContinuePrompt = confirmContinuePrompt;
        _errorPrompt = errorPrompt;
        _copyTextAction = copyTextAction;
    }

    public void ConfigureOverwriteHooks(
        Func<Task> prepareOverwriteReleaseAsync,
        Func<string, Task> reloadAfterOverwriteAsync)
    {
        _prepareOverwriteReleaseAsync = prepareOverwriteReleaseAsync;
        _reloadAfterOverwriteAsync = reloadAfterOverwriteAsync;
    }

    public void SetInitialEncoder(EncoderSelection encoder)
    {
        _preferredEncoderToRestore = encoder;
        SelectedEncoder = encoder;
        // Reappliquer la preference apres SetInitialEncoder car OnSelectedEncoderChanged la reinitialise.
        _preferredEncoderToRestore = encoder;
    }

    public void ConfigureOutputLocation(OutputFolderMode folderMode, string customFolder)
    {
        _outputFolderMode = folderMode;
        _customOutputFolder = customFolder?.Trim() ?? string.Empty;
        OutputFolder = ResolveTargetOutputFolder(_currentMedia);
        OnPropertyChanged(nameof(PreviewFileName));
    }

    public void OnVideoOpened(MediaInfo media)
    {
        _currentMedia = media;
        OutputFolder = ResolveTargetOutputFolder(media);
        HasExportErrors = false;
        Summary = string.Empty;
        EstimatedRemaining = string.Empty;
        GlobalProgress = 0.0;
        Items.Clear();
        _lastExportedFiles.Clear();
        NotifySegmentsChanged();
    }

    public void UpdateCurrentMediaWithoutReset(MediaInfo media)
    {
        _currentMedia = media;
        OutputFolder = ResolveTargetOutputFolder(media);
        NotifySegmentsChanged();
    }

    private string ResolveTargetOutputFolder(MediaInfo? media)
    {
        if (_outputFolderMode == OutputFolderMode.CustomFolder && !string.IsNullOrWhiteSpace(_customOutputFolder))
        {
            return _customOutputFolder;
        }

        return media is not null
            ? Path.GetDirectoryName(media.Path) ?? string.Empty
            : string.Empty;
    }

    public void NotifySegmentsChanged()
    {
        (_, IReadOnlyList<Segment> segments, _) = GetSourceSnapshot();
        bool anyEnabled = false;
        for (int i = 0; i < segments.Count; i++)
        {
            if (segments[i].Enabled)
            {
                anyEnabled = true;
                break;
            }
        }

        _hasEnabledSegments = anyEnabled;
        OnPropertyChanged(nameof(PreviewFileName));
        OnPropertyChanged(nameof(CanStartExport));
        ExportSeparateSegmentsCommand.NotifyCanExecuteChanged();
        ExportSingleVideoCommand.NotifyCanExecuteChanged();
    }

    public void ClearTransientErrorState()
    {
        if (!IsExporting && HasExportErrors)
        {
            HasExportErrors = false;
        }
    }

    public async Task DetectEncodersInBackgroundAsync()
    {
        _encoderDetectCts?.Cancel();
        _encoderDetectCts?.Dispose();
        var cts = new CancellationTokenSource();
        _encoderDetectCts = cts;

        EncoderStatus = "Encodeurs : détection…";

        try
        {
            EncoderCapabilities caps = await Task.Run(
                () => _ffmpegService.DetectEncodersAsync(cts.Token),
                cts.Token);

            if (cts.IsCancellationRequested || _disposed)
            {
                return;
            }

            _capabilities = caps;
            UpdateEncoderOptions(caps);
        }
        catch (OperationCanceledException)
        {
            // Annulation normale a la fermeture.
        }
        catch (Exception ex)
        {
            _logger.Warn($"Erreur lors de la détection des encodeurs : {ex.Message}");
            if (!_disposed)
            {
                _capabilities = new EncoderCapabilities(new HashSet<EncoderKind> { EncoderKind.Cpu });
                UpdateEncoderOptions(_capabilities);
            }
        }
    }

    public async Task<bool> CancelAndWaitAsync(TimeSpan timeout)
    {
        Task? activeTask = _activeExportTask;
        if (!IsExporting || activeTask is null)
        {
            return true;
        }

        _logger.Info("Annulation de l'export en cours demandée...");
        _exportCts?.Cancel();

        try
        {
            await Task.WhenAny(activeTask, Task.Delay(timeout)).ConfigureAwait(true);
        }
        catch
        {
            // L'export en cours capture ses propres erreurs.
        }

        return activeTask.IsCompleted;
    }

    partial void OnCrfChanged(int value)
    {
        int clamped = Math.Clamp(value, 14, 28);
        if (clamped != value)
        {
            Crf = clamped;
        }
    }

    partial void OnSelectedEncoderChanged(EncoderSelection value)
    {
        _preferredEncoderToRestore = null;
    }

    [RelayCommand(CanExecute = nameof(CanStartExport))]
    private Task ExportSeparateSegmentsAsync() => ExportCoreAsync(ExportOutputType.SeparateSegments);

    [RelayCommand(CanExecute = nameof(CanStartExport))]
    private Task ExportSingleVideoAsync() => ExportCoreAsync(ExportOutputType.SingleVideo);

    private async Task ExportCoreAsync(ExportOutputType outputType)
    {
        (MediaInfo? media, IReadOnlyList<Segment> segmentsSnapshot, IReadOnlyList<double> keyframesSnapshot) =
            GetSourceSnapshot();

        if (media is null || IsExporting)
        {
            return;
        }

        var enabledSegments = new List<Segment>(segmentsSnapshot.Count);
        for (int i = 0; i < segmentsSnapshot.Count; i++)
        {
            if (segmentsSnapshot[i].Enabled)
            {
                enabledSegments.Add(segmentsSnapshot[i]);
            }
        }

        if (enabledSegments.Count == 0)
        {
            if (_errorPrompt is not null)
            {
                await _errorPrompt.Invoke("Aucun segment inclus à exporter.");
            }
            return;
        }

        bool isOverwriteSource = _outputFolderMode == OutputFolderMode.OverwriteSource;
        if (isOverwriteSource && outputType == ExportOutputType.SeparateSegments && enabledSegments.Count > 1)
        {
            if (_errorPrompt is not null)
            {
                await _errorPrompt.Invoke(
                    "L'option « Écraser la vidéo source » ne peut remplacer le fichier d'origine que par une seule vidéo.\n\nUtilisez « Exporter en une seule vidéo » ou n'incluez qu'un seul segment.");
            }
            return;
        }

        HasExportErrors = false;
        string targetFolder = OutputFolder.Trim();

        // 1. Validation du dossier de sortie
        (bool folderOk, string? folderError) = await Task.Run(
            () => ExportPlanner.ValidateOutputDirectory(targetFolder));

        if (!folderOk)
        {
            if (_errorPrompt is not null)
            {
                await _errorPrompt.Invoke(folderError ?? "Dossier de sortie invalide.");
            }
            return;
        }

        OutputFolder = targetFolder;

        // 2. Verification des coupes hors keyframe en mode Rapide
        (EncoderKind resolvedEncoder, bool allowCpuFallback) = ResolveEncoderChoice();
        HashSet<Guid>? preciseOverrideIds = null;
        ExportMode batchDefaultMode = Mode;
        bool allowPerSegmentPrecise = outputType == ExportOutputType.SeparateSegments && !isOverwriteSource;

        if (Mode == ExportMode.Fast)
        {
            if (keyframesSnapshot.Count == 0)
            {
                FastCutWarningChoice choice = _fastCutWarningPrompt is not null
                    ? await _fastCutWarningPrompt.Invoke(0, true, allowPerSegmentPrecise)
                    : FastCutWarningChoice.FastAnyway;
                if (choice == FastCutWarningChoice.Cancel)
                {
                    return;
                }
            }
            else
            {
                var outOfKf = new HashSet<Guid>();
                for (int i = 0; i < enabledSegments.Count; i++)
                {
                    Segment seg = enabledSegments[i];
                    if (CutAccuracy.NeedsPreciseCut(seg, keyframesSnapshot, media.Fps))
                    {
                        outOfKf.Add(seg.Id);
                    }
                }

                if (outOfKf.Count > 0)
                {
                    FastCutWarningChoice choice = _fastCutWarningPrompt is not null
                        ? await _fastCutWarningPrompt.Invoke(outOfKf.Count, false, allowPerSegmentPrecise)
                        : FastCutWarningChoice.FastAnyway;

                    switch (choice)
                    {
                        case FastCutWarningChoice.Cancel:
                            return;

                        case FastCutWarningChoice.PreciseForOutOfKeyframe:
                            if (allowPerSegmentPrecise)
                            {
                                preciseOverrideIds = outOfKf;
                            }
                            else
                            {
                                batchDefaultMode = ExportMode.Precise;
                            }
                            break;

                        case FastCutWarningChoice.AllPrecise:
                            batchDefaultMode = ExportMode.Precise;
                            break;
                    }
                }
            }
        }

        if (outputType == ExportOutputType.SingleVideo || (isOverwriteSource && enabledSegments.Count == 1))
        {
            await PrepareAndExecuteSingleVideoAsync(
                media,
                enabledSegments,
                keyframesSnapshot,
                batchDefaultMode,
                resolvedEncoder,
                allowCpuFallback,
                isOverwriteSource);
            return;
        }

        List<ExportJob> jobs = BuildExportJobs(
            media,
            enabledSegments,
            batchDefaultMode,
            preciseOverrideIds,
            resolvedEncoder,
            allowCpuFallback);

        // 3. Verification de l'espace disque disponible
        long requiredBytes = ExportPlanner.EstimateRequiredBytes(media, jobs);
        if (!await CheckDiskSpaceAsync(requiredBytes, jobs.Any(j => j.Mode == ExportMode.Precise)))
        {
            return;
        }

        // 4. Detection des fichiers existants
        var conflicts = new List<string>();
        for (int i = 0; i < jobs.Count; i++)
        {
            if (File.Exists(jobs[i].OutputPath))
            {
                conflicts.Add(jobs[i].OutputPath);
            }
        }

        if (conflicts.Count > 0)
        {
            ConflictPolicy? policy = _conflictPrompt is not null
                ? await _conflictPrompt.Invoke(conflicts)
                : ConflictPolicy.AutoRename;
            if (policy is null)
            {
                return;
            }

            jobs = ExportPlanner.ResolveConflicts(jobs, policy.Value);
        }

        Task runTask = ExecuteExportBatchAsync(jobs);
        _activeExportTask = runTask;
        await runTask;
    }

    private async Task PrepareAndExecuteSingleVideoAsync(
        MediaInfo media,
        List<Segment> enabledSegments,
        IReadOnlyList<double> keyframesSnapshot,
        ExportMode effectiveMode,
        EncoderKind resolvedEncoder,
        bool allowCpuFallback,
        bool isOverwriteSource)
    {
        long requiredBytes = ExportPlanner.EstimateRequiredBytes(media, enabledSegments);
        if (!await CheckDiskSpaceAsync(requiredBytes, effectiveMode == ExportMode.Precise))
        {
            return;
        }

        string outputPath;
        string? overwriteSourcePath = null;
        string? overwriteFinalPath = null;
        bool isSkipped = false;

        if (isOverwriteSource)
        {
            (string stagingPath, string finalOverwritePath) = ExportPlanner.BuildOverwritePaths(media.Path, effectiveMode);
            string confirmMessage = string.Equals(finalOverwritePath, media.Path, StringComparison.OrdinalIgnoreCase)
                ? $"La vidéo source va être définitivement remplacée par la vidéo exportée :\n{media.Path}\n\nVoulez-vous continuer ?"
                : $"La vidéo source va être définitivement remplacée par la vidéo exportée (conversion vers {Path.GetExtension(finalOverwritePath)}) :\n{media.Path}\n→ {finalOverwritePath}\n\nVoulez-vous continuer ?";

            if (_confirmContinuePrompt is not null && !await _confirmContinuePrompt.Invoke(confirmMessage))
            {
                return;
            }

            outputPath = stagingPath;
            overwriteSourcePath = media.Path;
            overwriteFinalPath = finalOverwritePath;
        }
        else
        {
            string sourceName = Path.GetFileNameWithoutExtension(media.Path);
            string sourceExt = Path.GetExtension(media.Path);
            outputPath = ExportPlanner.BuildSingleVideoOutputPath(
                OutputFolder,
                FileNamePattern,
                sourceName,
                sourceExt,
                enabledSegments,
                effectiveMode);

            if (File.Exists(outputPath))
            {
                ConflictPolicy? policy = _conflictPrompt is not null
                    ? await _conflictPrompt.Invoke([outputPath])
                    : ConflictPolicy.AutoRename;
                if (policy is null)
                {
                    return;
                }

                (outputPath, isSkipped) = ExportPlanner.ResolveSingleVideoConflict(outputPath, policy.Value);
            }
        }

        var job = new SingleVideoExportJob(
            JobId: Guid.NewGuid(),
            Media: media,
            Segments: enabledSegments,
            Keyframes: keyframesSnapshot,
            OutputPath: outputPath,
            Mode: effectiveMode,
            Encoder: resolvedEncoder,
            Crf: Crf,
            AllowCpuFallback: allowCpuFallback,
            IsSkipped: isSkipped);

        Task runTask = ExecuteSingleVideoExportAsync(job, overwriteSourcePath, overwriteFinalPath);
        _activeExportTask = runTask;
        await runTask;
    }

    private async Task<bool> CheckDiskSpaceAsync(long requiredBytes, bool hasPrecise)
    {
        long? freeBytes = ExportPlanner.GetAvailableFreeSpaceBytes(OutputFolder);
        if (freeBytes is not long available || requiredBytes <= available)
        {
            return true;
        }

        string neededMb = FormatSizeMb(requiredBytes);
        string freeMb = FormatSizeMb(available);

        if (!hasPrecise)
        {
            if (_errorPrompt is not null)
            {
                await _errorPrompt.Invoke(
                    $"Espace disque insuffisant dans le dossier de sortie.\n\nRequis estimé : {neededMb}\nDisponible : {freeMb}");
            }
            return false;
        }

        return _confirmContinuePrompt is not null
            && await _confirmContinuePrompt.Invoke(
                $"L'espace disque disponible semble faible par rapport à la taille estimée.\n\nRequis estimé : {neededMb}\nDisponible : {freeMb}\n\nContinuer l'export malgré tout ?");
    }

    private async Task ExecuteSingleVideoExportAsync(
        SingleVideoExportJob job,
        string? overwriteSourcePath = null,
        string? overwriteFinalPath = null)
    {
        bool isOverwrite = !string.IsNullOrEmpty(overwriteSourcePath) && !string.IsNullOrEmpty(overwriteFinalPath);
        string displayFileName = Path.GetFileName(isOverwrite ? overwriteFinalPath! : job.OutputPath);

        Items.Clear();
        _lastExportedFiles.Clear();
        Summary = string.Empty;
        EstimatedRemaining = string.Empty;
        GlobalProgress = 0.0;
        HasExportErrors = false;

        string itemLabel = job.Segments.Count == 1
            ? job.Segments[0].Name
            : $"{job.Segments.Count} segment(s) fusionné(s)";

        var item = new ExportItemViewModel(
            job.JobId,
            itemLabel,
            displayFileName);

        if (job.IsSkipped)
        {
            item.Status = ExportItemStatus.Ignore;
            item.Message = "Ignoré (fichier existant)";
            item.Fraction = 1.0;
            Items.Add(item);
            GlobalProgress = 1.0;
            Summary = "Export ignoré (fichier existant)";
            return;
        }

        item.Status = ExportItemStatus.EnCours;
        item.Message = "En cours…";
        Items.Add(item);

        _exportCts?.Dispose();
        _exportCts = new CancellationTokenSource();
        CancellationToken ct = _exportCts.Token;

        IsExporting = true;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            SetThreadExecutionState(ExecutionState.EsContinuous | ExecutionState.EsSystemRequired);
        }
        catch
        {
            // Ignorer si SetThreadExecutionState echoue.
        }

        var progress = new Progress<ExportProgress>(p =>
        {
            if (_disposed || p.SegmentId != job.JobId)
            {
                return;
            }

            item.Fraction = p.Fraction;
            GlobalProgress = Math.Clamp(p.Fraction, 0.0, 1.0);
            if (job.Mode == ExportMode.Precise && GlobalProgress >= 0.02 && GlobalProgress < 0.999)
            {
                double elapsedSec = stopwatch.Elapsed.TotalSeconds;
                double totalEstimatedSec = elapsedSec / GlobalProgress;
                TimeSpan rem = TimeSpan.FromSeconds(Math.Max(0.0, totalEstimatedSec - elapsedSec));
                EstimatedRemaining = $"reste ~ {(int)rem.TotalMinutes:D2}:{rem.Seconds:D2}";
            }
        });

        string? reloadPathAfterFinally = null;
        string? replaceErrorMessage = null;

        try
        {
            ExportResult result = await _exportQueue.RunSingleVideoAsync(job, progress, ct);
            stopwatch.Stop();

            if (result.Outcome == ExportOutcome.Success && isOverwrite)
            {
                item.Message = "Remplacement de la vidéo source…";
                try
                {
                    if (_prepareOverwriteReleaseAsync is not null)
                    {
                        await _prepareOverwriteReleaseAsync.Invoke();
                    }

                    await ReplaceSourceFileAsync(job.OutputPath, overwriteSourcePath!, overwriteFinalPath!);

                    ApplyResultToItem(item, result with { FinalPath = overwriteFinalPath });
                    GlobalProgress = 1.0;
                    _lastExportedFiles.Add(overwriteFinalPath!);
                    Summary = $"Vidéo source remplacée ({job.Segments.Count} segment(s), {result.Elapsed.TotalSeconds:F1} s)";
                    reloadPathAfterFinally = overwriteFinalPath;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Impossible de remplacer la vidéo source '{overwriteSourcePath}' par '{job.OutputPath}'", ex);
                    HasExportErrors = true;
                    item.Status = ExportItemStatus.Erreur;
                    item.Message = "Fichier source verrouillé ; export conservé";
                    item.TechnicalDetails = ex.ToString();
                    Summary = "Échec du remplacement de la vidéo source";
                    reloadPathAfterFinally = overwriteSourcePath;
                    replaceErrorMessage =
                        $"Impossible de remplacer la vidéo source car le fichier est verrouillé par une autre application.\n\nLe résultat exporté a été conservé ici :\n{job.OutputPath}";
                }
            }
            else
            {
                if (isOverwrite)
                {
                    await ExportService.DeleteFileWithRetriesAsync(job.OutputPath, _logger);
                }

                ApplyResultToItem(item, result);
                if (result.Outcome == ExportOutcome.Success)
                {
                    GlobalProgress = 1.0;
                    if (!string.IsNullOrEmpty(result.FinalPath))
                    {
                        _lastExportedFiles.Add(result.FinalPath);
                    }

                    Summary = $"Vidéo unique exportée ({job.Segments.Count} segment(s), {result.Elapsed.TotalSeconds:F1} s)";
                }
                else if (result.Outcome == ExportOutcome.Cancelled)
                {
                    Summary = "Export annulé";
                }
                else if (result.Outcome == ExportOutcome.Skipped)
                {
                    GlobalProgress = 1.0;
                    Summary = "Export ignoré (fichier existant)";
                }
                else
                {
                    HasExportErrors = true;
                    Summary = "Échec de l'export de la vidéo unique";
                }
            }

            _logger.Info($"Export vidéo unique terminé : {Summary}");

            if (!_disposed && result.Outcome != ExportOutcome.Cancelled)
            {
                try
                {
                    MessageBeep(MbIconAsterisk);
                }
                catch
                {
                    // Ignorer si aucun peripherique audio n'est disponible.
                }
            }
        }
        finally
        {
            try
            {
                SetThreadExecutionState(ExecutionState.EsContinuous);
            }
            catch
            {
                // Ignorer si SetThreadExecutionState echoue.
            }

            IsExporting = false;
            EstimatedRemaining = string.Empty;
            _activeExportTask = null;
            _exportCts?.Dispose();
            _exportCts = null;
            OpenOutputFolderCommand.NotifyCanExecuteChanged();
        }

        if (reloadPathAfterFinally is not null && _reloadAfterOverwriteAsync is not null && !_disposed)
        {
            await _reloadAfterOverwriteAsync.Invoke(reloadPathAfterFinally);
        }

        if (replaceErrorMessage is not null && _errorPrompt is not null && !_disposed)
        {
            await _errorPrompt.Invoke(replaceErrorMessage);
        }
    }

    private async Task ReplaceSourceFileAsync(string stagingPath, string sourcePath, string finalOverwritePath)
    {
        const int maxAttempts = 10;
        const int retryDelayMs = 150;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                File.Move(stagingPath, finalOverwritePath, overwrite: true);
                break;
            }
            catch (IOException) when (attempt < maxAttempts - 1)
            {
                await Task.Delay(retryDelayMs);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts - 1)
            {
                await Task.Delay(retryDelayMs);
            }
        }

        if (!string.Equals(finalOverwritePath, sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            await ExportService.DeleteFileWithRetriesAsync(sourcePath, _logger);
        }
    }

    [RelayCommand(CanExecute = nameof(IsExporting))]
    private void Cancel()
    {
        if (!IsExporting)
        {
            return;
        }

        _logger.Info("Annulation de l'export demandée par l'utilisateur.");
        _exportCts?.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanOpenOutputFolder))]
    private void OpenOutputFolder()
    {
        try
        {
            string? selectTarget = _lastExportedFiles.FirstOrDefault(File.Exists);
            if (!string.IsNullOrEmpty(selectTarget))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{selectTarget}\"")
                {
                    UseShellExecute = true
                });
                return;
            }

            if (!string.IsNullOrWhiteSpace(OutputFolder) && Directory.Exists(OutputFolder))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{OutputFolder}\"")
                {
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Impossible d'ouvrir le dossier de sortie : {ex.Message}");
        }
    }

    private bool CanOpenOutputFolder()
        => !string.IsNullOrWhiteSpace(OutputFolder) && Directory.Exists(OutputFolder);

    [RelayCommand]
    private async Task CopyItemTechnicalDetailsAsync(ExportItemViewModel? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.TechnicalDetails) || _copyTextAction is null)
        {
            return;
        }

        try
        {
            await _copyTextAction.Invoke(item.TechnicalDetails);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Impossible de copier les détails techniques : {ex.Message}");
        }
    }

    private async Task ExecuteExportBatchAsync(List<ExportJob> jobs)
    {
        Items.Clear();
        _lastExportedFiles.Clear();
        Summary = string.Empty;
        EstimatedRemaining = string.Empty;
        GlobalProgress = 0.0;
        HasExportErrors = false;

        var itemBySegmentId = new Dictionary<Guid, int>(jobs.Count);
        double[] segmentDurations = new double[jobs.Count];
        double[] itemFractions = new double[jobs.Count];
        double totalWeightSeconds = 0.0;
        bool hasAnyPreciseJob = false;

        for (int i = 0; i < jobs.Count; i++)
        {
            ExportJob job = jobs[i];
            var vm = new ExportItemViewModel(
                job.Segment.Id,
                job.Segment.Name,
                Path.GetFileName(job.OutputPath));

            if (job.IsSkipped)
            {
                vm.Status = ExportItemStatus.Ignore;
                vm.Message = "Ignoré (fichier existant)";
                vm.Fraction = 1.0;
                itemFractions[i] = 1.0;
            }
            else
            {
                double weight = Math.Max(0.1, job.Segment.Duration.TotalSeconds);
                segmentDurations[i] = weight;
                totalWeightSeconds += weight;
                if (job.Mode == ExportMode.Precise)
                {
                    hasAnyPreciseJob = true;
                }
            }

            itemBySegmentId[job.Segment.Id] = i;
            Items.Add(vm);
        }

        int maxConcurrency = DetermineMaxConcurrency(jobs);
        _exportCts?.Dispose();
        _exportCts = new CancellationTokenSource();
        CancellationToken ct = _exportCts.Token;

        IsExporting = true;
        var batchStopwatch = Stopwatch.StartNew();

        try
        {
            SetThreadExecutionState(ExecutionState.EsContinuous | ExecutionState.EsSystemRequired);
        }
        catch
        {
            // Ignorer si SetThreadExecutionState echoue.
        }

        var progress = new Progress<ExportProgress>(p =>
        {
            if (_disposed || !itemBySegmentId.TryGetValue(p.SegmentId, out int idx))
            {
                return;
            }

            ExportItemViewModel item = Items[idx];
            if (item.Status == ExportItemStatus.EnAttente)
            {
                item.Status = ExportItemStatus.EnCours;
                item.Message = "En cours…";
            }

            item.Fraction = p.Fraction;
            itemFractions[idx] = p.Fraction;

            if (totalWeightSeconds > 0.0)
            {
                double weightedDone = 0.0;
                for (int k = 0; k < jobs.Count; k++)
                {
                    weightedDone += segmentDurations[k] * itemFractions[k];
                }

                GlobalProgress = Math.Clamp(weightedDone / totalWeightSeconds, 0.0, 1.0);
                if (hasAnyPreciseJob && GlobalProgress >= 0.02 && GlobalProgress < 0.999)
                {
                    double elapsedSec = batchStopwatch.Elapsed.TotalSeconds;
                    double totalEstimatedSec = elapsedSec / GlobalProgress;
                    TimeSpan rem = TimeSpan.FromSeconds(Math.Max(0.0, totalEstimatedSec - elapsedSec));
                    EstimatedRemaining = $"reste ~ {(int)rem.TotalMinutes:D2}:{rem.Seconds:D2}";
                }
            }
        });

        void OnJobStarted(int jobIdx)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed || jobIdx < 0 || jobIdx >= Items.Count)
                {
                    return;
                }

                ExportItemViewModel item = Items[jobIdx];
                if (item.Status == ExportItemStatus.EnAttente)
                {
                    item.Status = ExportItemStatus.EnCours;
                    item.Message = "En cours…";
                }
            });
        }

        void OnJobCompleted(int jobIdx, ExportResult res)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed || jobIdx < 0 || jobIdx >= Items.Count)
                {
                    return;
                }

                ApplyResultToItem(Items[jobIdx], res);
                if (res.Outcome is ExportOutcome.Success or ExportOutcome.Skipped or ExportOutcome.Failed)
                {
                    itemFractions[jobIdx] = res.Outcome == ExportOutcome.Success ? 1.0 : itemFractions[jobIdx];
                }
            });
        }

        try
        {
            IReadOnlyList<ExportResult> results = await _exportQueue.RunAsync(
                jobs,
                maxConcurrency,
                progress,
                OnJobStarted,
                OnJobCompleted,
                ct);

            batchStopwatch.Stop();

            int successCount = 0;
            int errorCount = 0;
            int skippedCount = 0;
            int cancelledCount = 0;

            for (int i = 0; i < results.Count; i++)
            {
                ExportResult res = results[i];
                if (i < Items.Count)
                {
                    ApplyResultToItem(Items[i], res);
                }

                switch (res.Outcome)
                {
                    case ExportOutcome.Success:
                        successCount++;
                        if (!string.IsNullOrEmpty(res.FinalPath))
                        {
                            _lastExportedFiles.Add(res.FinalPath);
                        }
                        break;

                    case ExportOutcome.Failed:
                        errorCount++;
                        break;

                    case ExportOutcome.Skipped:
                        skippedCount++;
                        break;

                    case ExportOutcome.Cancelled:
                        cancelledCount++;
                        break;
                }
            }

            if (errorCount == 0 && cancelledCount == 0)
            {
                GlobalProgress = 1.0;
            }

            Summary = BuildSummaryText(jobs.Count, successCount, errorCount, skippedCount, cancelledCount);
            HasExportErrors = errorCount > 0;
            _logger.Info($"Export terminé : {Summary}");

            if (!_disposed && cancelledCount == 0)
            {
                try
                {
                    MessageBeep(MbIconAsterisk);
                }
                catch
                {
                    // Ignorer si aucun peripherique audio n'est disponible.
                }
            }
        }
        finally
        {
            try
            {
                SetThreadExecutionState(ExecutionState.EsContinuous);
            }
            catch
            {
                // Ignorer si SetThreadExecutionState echoue.
            }

            IsExporting = false;
            EstimatedRemaining = string.Empty;
            _activeExportTask = null;
            _exportCts?.Dispose();
            _exportCts = null;
            OpenOutputFolderCommand.NotifyCanExecuteChanged();
        }
    }

    private List<ExportJob> BuildExportJobs(
        MediaInfo media,
        List<Segment> enabledSegments,
        ExportMode defaultMode,
        HashSet<Guid>? preciseOverrideIds,
        EncoderKind encoder,
        bool allowCpuFallback)
    {
        if (preciseOverrideIds is null || preciseOverrideIds.Count == 0)
        {
            return ExportPlanner.BuildJobs(
                media,
                enabledSegments,
                OutputFolder,
                FileNamePattern,
                defaultMode,
                encoder,
                Crf,
                allowCpuFallback);
        }

        int total = enabledSegments.Count;
        string sourceName = Path.GetFileNameWithoutExtension(media.Path);
        string sourceExt = Path.GetExtension(media.Path);
        var jobs = new List<ExportJob>(total);

        for (int i = 0; i < total; i++)
        {
            Segment seg = enabledSegments[i];
            ExportMode segMode = preciseOverrideIds.Contains(seg.Id) ? ExportMode.Precise : ExportMode.Fast;
            string outPath = ExportPlanner.BuildFullOutputPath(
                OutputFolder,
                FileNamePattern,
                sourceName,
                sourceExt,
                i + 1,
                total,
                seg,
                segMode);

            jobs.Add(new ExportJob(
                Media: media,
                Segment: seg,
                Index: i + 1,
                Total: total,
                OutputPath: outPath,
                Mode: segMode,
                Encoder: encoder,
                Crf: Crf,
                AllowCpuFallback: allowCpuFallback));
        }

        return jobs;
    }

    private (EncoderKind Encoder, bool AllowCpuFallback) ResolveEncoderChoice()
    {
        switch (SelectedEncoder)
        {
            case EncoderSelection.Cpu:
                return (EncoderKind.Cpu, false);

            case EncoderSelection.Nvenc:
                return (EncoderKind.Nvenc, false);

            case EncoderSelection.Qsv:
                return (EncoderKind.Qsv, false);

            case EncoderSelection.Amf:
                return (EncoderKind.Amf, false);

            case EncoderSelection.Auto:
            default:
                if (_capabilities.Available.Contains(EncoderKind.Nvenc))
                {
                    return (EncoderKind.Nvenc, true);
                }

                if (_capabilities.Available.Contains(EncoderKind.Qsv))
                {
                    return (EncoderKind.Qsv, true);
                }

                if (_capabilities.Available.Contains(EncoderKind.Amf))
                {
                    return (EncoderKind.Amf, true);
                }

                return (EncoderKind.Cpu, false);
        }
    }

    private static int DetermineMaxConcurrency(IReadOnlyList<ExportJob> jobs)
    {
        for (int i = 0; i < jobs.Count; i++)
        {
            ExportJob job = jobs[i];
            if (!job.IsSkipped && job.Mode == ExportMode.Precise && job.Encoder == EncoderKind.Cpu)
            {
                return 1;
            }
        }

        return 2;
    }

    private static void ApplyResultToItem(ExportItemViewModel item, ExportResult result)
    {
        switch (result.Outcome)
        {
            case ExportOutcome.Success:
                item.Status = ExportItemStatus.Termine;
                item.Fraction = 1.0;
                item.Message = $"Terminé ({result.Elapsed.TotalSeconds:F1} s)";
                item.TechnicalDetails = null;
                break;

            case ExportOutcome.Failed:
                item.Status = ExportItemStatus.Erreur;
                item.Message = result.ErrorMessage ?? "Erreur lors de l'export";
                item.TechnicalDetails = result.TechnicalDetails;
                break;

            case ExportOutcome.Cancelled:
                item.Status = ExportItemStatus.Annule;
                item.Message = "Annulé";
                item.TechnicalDetails = null;
                break;

            case ExportOutcome.Skipped:
                item.Status = ExportItemStatus.Ignore;
                item.Fraction = 1.0;
                item.Message = "Ignoré (existant)";
                item.TechnicalDetails = null;
                break;
        }
    }

    private void UpdateEncoderOptions(EncoderCapabilities caps)
    {
        var names = new List<string> { "CPU" };

        foreach (EncoderOptionItem option in EncoderOptions)
        {
            switch (option.Kind)
            {
                case EncoderSelection.Auto:
                case EncoderSelection.Cpu:
                    option.IsEnabled = true;
                    break;

                case EncoderSelection.Nvenc:
                    option.IsEnabled = caps.Available.Contains(EncoderKind.Nvenc);
                    if (option.IsEnabled)
                    {
                        names.Add("NVENC");
                    }
                    break;

                case EncoderSelection.Qsv:
                    option.IsEnabled = caps.Available.Contains(EncoderKind.Qsv);
                    if (option.IsEnabled)
                    {
                        names.Add("QSV");
                    }
                    break;

                case EncoderSelection.Amf:
                    option.IsEnabled = caps.Available.Contains(EncoderKind.Amf);
                    if (option.IsEnabled)
                    {
                        names.Add("AMF");
                    }
                    break;
            }
        }

        EncoderStatus = $"Encodeurs : {string.Join(", ", names)}";

        EncoderSelection targetToValidate = _preferredEncoderToRestore ?? SelectedEncoder;
        _preferredEncoderToRestore = null;

        bool isTargetAvailable = false;
        foreach (EncoderOptionItem option in EncoderOptions)
        {
            if (option.Kind == targetToValidate && option.IsEnabled)
            {
                isTargetAvailable = true;
                break;
            }
        }

        SelectedEncoder = isTargetAvailable ? targetToValidate : EncoderSelection.Auto;
    }

    private (MediaInfo? Media, IReadOnlyList<Segment> Segments, IReadOnlyList<double> Keyframes) GetSourceSnapshot()
    {
        return _sourceProvider is not null
            ? _sourceProvider.Invoke()
            : (_currentMedia, Array.Empty<Segment>(), Array.Empty<double>());
    }

    private static string BuildSummaryText(
        int total,
        int success,
        int errors,
        int skipped,
        int cancelled)
    {
        var parts = new List<string> { $"{success} / {total} segment(s) exporté(s)" };
        if (errors > 0)
        {
            parts.Add($"{errors} erreur(s)");
        }

        if (skipped > 0)
        {
            parts.Add($"{skipped} ignoré(s)");
        }

        if (cancelled > 0)
        {
            parts.Add($"{cancelled} annulé(s)");
        }

        return string.Join(", ", parts);
    }

    private static string FormatSizeMb(long bytes)
    {
        double mb = bytes / (1024.0 * 1024.0);
        return mb >= 1024.0
            ? $"{mb / 1024.0:F2} Go"
            : $"{mb:F1} Mo";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _encoderDetectCts?.Cancel();
        _encoderDetectCts?.Dispose();
        _encoderDetectCts = null;

        _exportCts?.Cancel();
        _exportCts?.Dispose();
        _exportCts = null;
    }
}
