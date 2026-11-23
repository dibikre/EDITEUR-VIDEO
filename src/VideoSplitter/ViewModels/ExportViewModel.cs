using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Shell;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using VideoSplitter.Export;
using VideoSplitter.Models;
using VideoSplitter.Services;
using VideoSplitter.Views;

namespace VideoSplitter.ViewModels;

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

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern uint SetThreadExecutionState(ExecutionState esFlags);

    private readonly IFfmpegService _ffmpegService;
    private readonly IExportQueue _exportQueue;
    private readonly IAppLogger _logger;
    private readonly Dispatcher _dispatcher;
    private readonly List<string> _lastExportedFiles = [];

    private Func<(MediaInfo? Media, IReadOnlyList<Segment> Segments, IReadOnlyList<double> Keyframes)>? _sourceProvider;
    private Func<int, bool, FastCutWarningChoice>? _fastCutWarningPrompt;
    private Func<IReadOnlyList<string>, ConflictPolicy?>? _conflictPrompt;
    private Func<string, bool>? _confirmContinuePrompt;
    private Action<string>? _errorPrompt;

    private CancellationTokenSource? _encoderDetectCts;
    private CancellationTokenSource? _exportCts;
    private Task? _activeExportTask;
    private EncoderCapabilities _capabilities = new(new HashSet<EncoderKind> { EncoderKind.Cpu });
    private MediaInfo? _currentMedia;
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
    [NotifyCanExecuteChangedFor(
        nameof(ExportCommand),
        nameof(CancelCommand),
        nameof(BrowseFolderCommand),
        nameof(OpenOutputFolderCommand))]
    private bool _isExporting;

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

    public TaskbarItemProgressState TaskbarProgressState =>
        IsExporting
            ? TaskbarItemProgressState.Normal
            : (HasExportErrors ? TaskbarItemProgressState.Error : TaskbarItemProgressState.None);

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
        _dispatcher = Application.Current.Dispatcher;
    }

    public void ConfigureBridges(
        Func<(MediaInfo? Media, IReadOnlyList<Segment> Segments, IReadOnlyList<double> Keyframes)> sourceProvider,
        Func<int, bool, FastCutWarningChoice> fastCutWarningPrompt,
        Func<IReadOnlyList<string>, ConflictPolicy?> conflictPrompt,
        Func<string, bool> confirmContinuePrompt,
        Action<string> errorPrompt)
    {
        _sourceProvider = sourceProvider;
        _fastCutWarningPrompt = fastCutWarningPrompt;
        _conflictPrompt = conflictPrompt;
        _confirmContinuePrompt = confirmContinuePrompt;
        _errorPrompt = errorPrompt;
    }

    public void OnVideoOpened(MediaInfo media)
    {
        _currentMedia = media;
        OutputFolder = Path.GetDirectoryName(media.Path) ?? string.Empty;
        HasExportErrors = false;
        Summary = string.Empty;
        EstimatedRemaining = string.Empty;
        GlobalProgress = 0.0;
        Items.Clear();
        _lastExportedFiles.Clear();
        NotifySegmentsChanged();
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
        ExportCommand.NotifyCanExecuteChanged();
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

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
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
            _errorPrompt?.Invoke("Aucun segment inclus à exporter.");
            return;
        }

        HasExportErrors = false;
        string targetFolder = OutputFolder.Trim();

        // 1. Validation du dossier de sortie
        (bool folderOk, string? folderError) = await Task.Run(
            () => ExportPlanner.ValidateOutputDirectory(targetFolder));

        if (!folderOk)
        {
            _errorPrompt?.Invoke(folderError ?? "Dossier de sortie invalide.");
            return;
        }

        OutputFolder = targetFolder;

        // 2. Verification des coupes hors keyframe en mode Rapide
        (EncoderKind resolvedEncoder, bool allowCpuFallback) = ResolveEncoderChoice();
        HashSet<Guid>? preciseOverrideIds = null;
        ExportMode batchDefaultMode = Mode;

        if (Mode == ExportMode.Fast)
        {
            if (keyframesSnapshot.Count == 0)
            {
                FastCutWarningChoice choice = _fastCutWarningPrompt?.Invoke(0, true) ?? FastCutWarningChoice.FastAnyway;
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
                    FastCutWarningChoice choice =
                        _fastCutWarningPrompt?.Invoke(outOfKf.Count, false) ?? FastCutWarningChoice.FastAnyway;

                    switch (choice)
                    {
                        case FastCutWarningChoice.Cancel:
                            return;

                        case FastCutWarningChoice.PreciseForOutOfKeyframe:
                            preciseOverrideIds = outOfKf;
                            break;

                        case FastCutWarningChoice.AllPrecise:
                            batchDefaultMode = ExportMode.Precise;
                            break;
                    }
                }
            }
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
        long? freeBytes = ExportPlanner.GetAvailableFreeSpaceBytes(OutputFolder);
        if (freeBytes is long available && requiredBytes > available)
        {
            bool hasPrecise = jobs.Any(j => j.Mode == ExportMode.Precise);
            string neededMb = FormatSizeMb(requiredBytes);
            string freeMb = FormatSizeMb(available);

            if (!hasPrecise)
            {
                _errorPrompt?.Invoke(
                    $"Espace disque insuffisant dans le dossier de sortie.\n\nRequis estimé : {neededMb}\nDisponible : {freeMb}");
                return;
            }

            bool proceedLowSpace = _confirmContinuePrompt?.Invoke(
                $"L'espace disque disponible semble faible par rapport à la taille estimée.\n\nRequis estimé : {neededMb}\nDisponible : {freeMb}\n\nContinuer l'export malgré tout ?") ?? false;
            if (!proceedLowSpace)
            {
                return;
            }
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
            ConflictPolicy? policy = _conflictPrompt?.Invoke(conflicts);
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

    private bool CanExport() => !IsExporting && _currentMedia is not null && _hasEnabledSegments;

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

    [RelayCommand(CanExecute = nameof(CanBrowseFolder))]
    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choisir le dossier de sortie"
        };

        if (!string.IsNullOrWhiteSpace(OutputFolder) && Directory.Exists(OutputFolder))
        {
            dialog.InitialDirectory = OutputFolder;
        }

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            OutputFolder = dialog.FolderName;
        }
    }

    private bool CanBrowseFolder() => !IsExporting;

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
    private void CopyItemTechnicalDetails(ExportItemViewModel? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.TechnicalDetails))
        {
            return;
        }

        try
        {
            Clipboard.SetText(item.TechnicalDetails);
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
            _dispatcher.BeginInvoke(() =>
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
            _dispatcher.BeginInvoke(() =>
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
                    SystemSounds.Asterisk.Play();
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
