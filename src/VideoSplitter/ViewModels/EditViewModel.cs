using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoSplitter.Editing;
using VideoSplitter.Models;
using VideoSplitter.Services;

namespace VideoSplitter.ViewModels;

public sealed partial class EditViewModel : ObservableObject, IDisposable
{
    private const double MarkerNavigationEpsilonSeconds = 0.005;

    private readonly IFfmpegService _ffmpegService;
    private readonly IAppLogger _logger;
    private readonly EditHistory<IReadOnlyList<Segment>> _history = new();
    private readonly DispatcherTimer _statusTimer;

    private CancellationTokenSource? _keyframeCts;
    private MediaInfo? _currentMedia;
    private Func<(TimeSpan Position, double PixelsPerSecond)>? _playbackContextProvider;
    private Action<TimeSpan>? _seekAction;
    private int _nextSegmentNumber = 2;
    private bool _isSyncingSelection;
    private bool _disposed;

    public ObservableCollection<SegmentRowViewModel> SegmentRows { get; } = [];

    [ObservableProperty]
    private IReadOnlyList<Segment> _segments = Array.Empty<Segment>();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(
        nameof(ToggleEnabledCommand),
        nameof(MergeWithPreviousCommand),
        nameof(MergeWithNextCommand))]
    private Guid? _selectedSegmentId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteMarkerCommand))]
    private int? _selectedMarkerIndex;

    [ObservableProperty]
    private SegmentRowViewModel? _selectedSegmentRow;

    [ObservableProperty]
    private bool _snapToKeyframes = true;

    [ObservableProperty]
    private IReadOnlyList<double> _keyframes = Array.Empty<double>();

    [ObservableProperty]
    private string _keyframeStatus = string.Empty;

    [ObservableProperty]
    private TimeSpan? _pendingSegmentStart;

    [ObservableProperty]
    private string _editStatus = string.Empty;

    [ObservableProperty]
    private int _segmentCount;

    [ObservableProperty]
    private int _includedSegmentCount;

    [ObservableProperty]
    private TimeSpan _includedDuration;

    [ObservableProperty]
    private string _summaryText = "Aucun segment";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _canUndo;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RedoCommand))]
    private bool _canRedo;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(
        nameof(SplitCommand),
        nameof(SetInCommand),
        nameof(SetOutCommand),
        nameof(DeleteMarkerCommand),
        nameof(ToggleEnabledCommand),
        nameof(MergeWithPreviousCommand),
        nameof(MergeWithNextCommand),
        nameof(PreviousKeyframeCommand),
        nameof(NextKeyframeCommand),
        nameof(PreviousMarkerCommand),
        nameof(NextMarkerCommand))]
    private bool _hasMedia;

    public EditViewModel(IFfmpegService ffmpegService, IAppLogger logger)
    {
        _ffmpegService = ffmpegService;
        _logger = logger;

        _statusTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _statusTimer.Tick += OnStatusTimerTick;
    }

    public void ConfigurePlaybackBridge(
        Func<(TimeSpan Position, double PixelsPerSecond)> contextProvider,
        Action<TimeSpan> seekAction)
    {
        _playbackContextProvider = contextProvider;
        _seekAction = seekAction;
    }

    public void OnVideoOpened(MediaInfo media)
    {
        CancelKeyframeAnalysis();

        _currentMedia = media;
        _nextSegmentNumber = 2;
        _history.Clear();

        IReadOnlyList<Segment> initial = SegmentEditor.CreateInitial(media.Duration);
        if (initial.Count > 0)
        {
            _history.Push(initial);
        }

        HasMedia = initial.Count > 0;
        PendingSegmentStart = null;
        Keyframes = Array.Empty<double>();
        KeyframeStatus = HasMedia ? "Analyse keyframes..." : string.Empty;
        ClearTransientStatus();

        Segments = initial;
        SelectedMarkerIndex = null;
        SelectedSegmentId = initial.Count > 0 ? initial[0].Id : null;

        SyncSegmentRows();
        UpdateCountersAndCommandStates();

        if (HasMedia)
        {
            var cts = new CancellationTokenSource();
            _keyframeCts = cts;
            _ = LoadKeyframesAsync(media, cts);
        }
    }

    public void SelectSegment(Guid? segmentId)
    {
        SelectedSegmentId = segmentId;
    }

    public void SelectMarker(int? markerIndex)
    {
        if (markerIndex is int idx && (idx < 0 || idx >= Segments.Count - 1))
        {
            SelectedMarkerIndex = null;
            return;
        }

        SelectedMarkerIndex = markerIndex;
    }

    public void SeekToSegmentStart(Guid segmentId)
    {
        int idx = FindSegmentIndex(Segments, segmentId);
        if (idx >= 0)
        {
            _seekAction?.Invoke(Segments[idx].Start);
        }
    }

    public void ApplyBoundaryMove(int boundaryIndex, TimeSpan newTime)
    {
        if (!HasMedia || boundaryIndex < 0 || boundaryIndex >= Segments.Count - 1)
        {
            return;
        }

        if (Math.Abs((newTime - Segments[boundaryIndex].End).TotalSeconds) < 1e-6)
        {
            return;
        }

        SegmentEditResult result = SegmentEditor.MoveBoundary(Segments, boundaryIndex, newTime);
        if (!result.Success)
        {
            ShowTransientStatus(result.ErrorMessage ?? "Deplacement impossible.");
            return;
        }

        TimeSpan effective = result.Segments[boundaryIndex].End;
        _logger.Info($"Frontiere #{boundaryIndex + 1} deplacee a {effective:hh\\:mm\\:ss\\.fff}");

        Guid? selection = SelectedSegmentId ?? result.Segments[boundaryIndex].Id;
        Apply(result.Segments, selection, clearMarkerSelection: false, statusMessage: null);
        SelectedMarkerIndex = boundaryIndex;
    }

    public void SetSegmentEnabled(Guid id, bool enabled)
    {
        if (!HasMedia)
        {
            return;
        }

        SegmentEditResult result = SegmentEditor.SetEnabled(Segments, id, enabled);
        if (!result.Success)
        {
            ShowTransientStatus(result.ErrorMessage ?? "Modification impossible.");
            return;
        }

        if (ReferenceEquals(result.Segments, Segments))
        {
            return;
        }

        _logger.Info($"Segment {id} {(enabled ? "inclus" : "exclu")}");
        Apply(result.Segments, id, clearMarkerSelection: false);
    }

    public void RenameSegment(Guid id, string newName)
    {
        if (!HasMedia)
        {
            return;
        }

        int existingIndex = FindSegmentIndex(Segments, id);
        if (existingIndex < 0)
        {
            return;
        }

        SegmentEditResult result = SegmentEditor.Rename(Segments, id, newName);
        if (!result.Success)
        {
            RestoreRowName(id, Segments[existingIndex].Name);
            ShowTransientStatus(result.ErrorMessage ?? "Nom invalide.");
            return;
        }

        if (ReferenceEquals(result.Segments, Segments))
        {
            RestoreRowName(id, Segments[existingIndex].Name);
            return;
        }

        _logger.Info($"Segment renomme : '{result.Segments[existingIndex].Name}'");
        Apply(result.Segments, id, clearMarkerSelection: false);
    }

    [RelayCommand(CanExecute = nameof(CanSplit))]
    private void Split()
    {
        (TimeSpan pos, double pps) = GetPlaybackContext();
        TimeSpan effectivePos = pos;

        if (SnapToKeyframes && Keyframes.Count > 0)
        {
            double tolerance = Math.Min(8.0 / Math.Max(0.1, pps), 0.5);
            double snapped = KeyframeSnapper.Snap(pos.TotalSeconds, Keyframes, tolerance);
            effectivePos = TimeSpan.FromSeconds(snapped);
        }

        SegmentEditResult result = SegmentEditor.Split(Segments, effectivePos, _nextSegmentNumber);
        if (!result.Success)
        {
            ShowTransientStatus(result.ErrorMessage ?? "Coupe impossible.");
            return;
        }

        _nextSegmentNumber++;
        _logger.Info($"Split a {effectivePos:hh\\:mm\\:ss\\.fff} -> {result.Segments.Count} segment(s)");
        Apply(result.Segments, result.CreatedSegmentId, clearMarkerSelection: true);
    }

    private bool CanSplit() => HasMedia;

    public void SetPendingSegmentStart(TimeSpan target)
    {
        if (!HasMedia)
        {
            return;
        }

        TimeSpan maxDuration = _currentMedia?.Duration ?? (Segments.Count > 0 ? Segments[^1].End : TimeSpan.Zero);
        TimeSpan clamped = target < TimeSpan.Zero ? TimeSpan.Zero : (maxDuration > TimeSpan.Zero && target > maxDuration ? maxDuration : target);

        PendingSegmentStart = clamped;
        _statusTimer.Stop();
        EditStatus = BuildPendingStartStatus(clamped);
        _logger.Info($"Debut en attente defini a {clamped:hh\\:mm\\:ss\\.fff}");
    }

    public bool ClearPendingSegmentStart()
    {
        if (!PendingSegmentStart.HasValue)
        {
            return false;
        }

        PendingSegmentStart = null;
        ClearTransientStatus();
        return true;
    }

    public void CompleteSegmentWithEnd(TimeSpan target)
    {
        if (!HasMedia)
        {
            return;
        }

        if (PendingSegmentStart is TimeSpan start)
        {
            if (target <= start || target - start < SegmentEditor.MinDuration)
            {
                ShowTransientStatus("La fin doit etre apres le debut (>= 0,1 s).");
                return;
            }

            SegmentEditResult result = SegmentEditor.DefineSegment(Segments, start, target, _nextSegmentNumber);
            if (!result.Success)
            {
                ShowTransientStatus(result.ErrorMessage ?? "Definition du segment impossible.");
                return;
            }

            _nextSegmentNumber += Math.Max(0, result.AllocatedSegmentNames);
            PendingSegmentStart = null;
            _logger.Info($"Segment defini [{start:hh\\:mm\\:ss\\.fff} -> {target:hh\\:mm\\:ss\\.fff}]");
            Apply(result.Segments, result.CreatedSegmentId, clearMarkerSelection: true);
            return;
        }

        if (Segments.Count == 1)
        {
            SegmentEditResult initialResult = SegmentEditor.DefineSegment(Segments, TimeSpan.Zero, target, _nextSegmentNumber);
            if (!initialResult.Success)
            {
                ShowTransientStatus(initialResult.ErrorMessage ?? "Point de fin invalide.");
                return;
            }

            _nextSegmentNumber += Math.Max(0, initialResult.AllocatedSegmentNames);
            _logger.Info($"Segment defini [00:00:00.000 -> {target:hh\\:mm\\:ss\\.fff}]");
            Apply(initialResult.Segments, initialResult.CreatedSegmentId, clearMarkerSelection: true);
            return;
        }

        if (SelectedSegmentId is Guid selId
            && FindSegmentIndex(Segments, selId) is int selIdx
            && selIdx >= 0
            && Segments[selIdx].Enabled
            && target > Segments[selIdx].Start + SegmentEditor.MinDuration)
        {
            if (selIdx < Segments.Count - 1 && target < Segments[selIdx + 1].End - SegmentEditor.MinDuration)
            {
                ApplyBoundaryMove(selIdx, target);
                return;
            }

            if (selIdx == Segments.Count - 1)
            {
                SetOutAt(target);
                return;
            }
        }

        ShowTransientStatus("Definissez d'abord le debut par clic gauche.");
    }

    public void SetInAt(TimeSpan target)
    {
        if (!HasMedia)
        {
            return;
        }

        PendingSegmentStart = null;
        int previousCount = Segments.Count;
        SegmentEditResult result = SegmentEditor.SetIn(Segments, target, _nextSegmentNumber);
        if (!result.Success)
        {
            ShowTransientStatus(result.ErrorMessage ?? "Point d'entree invalide.");
            return;
        }

        if (result.Segments.Count > previousCount)
        {
            _nextSegmentNumber++;
        }

        int containingIdx = SegmentEditor.ContainingIndex(result.Segments, target);
        Guid? nextSelection = result.CreatedSegmentId
            ?? (containingIdx >= 0 ? result.Segments[containingIdx].Id : SelectedSegmentId);

        _logger.Info($"Point In defini a {target:hh\\:mm\\:ss\\.fff}");
        Apply(result.Segments, nextSelection, clearMarkerSelection: true);
    }

    public void SetOutAt(TimeSpan target)
    {
        if (!HasMedia)
        {
            return;
        }

        int previousCount = Segments.Count;
        SegmentEditResult result = SegmentEditor.SetOut(Segments, target, _nextSegmentNumber);
        if (!result.Success)
        {
            ShowTransientStatus(result.ErrorMessage ?? "Point de sortie invalide.");
            return;
        }

        if (result.Segments.Count > previousCount)
        {
            _nextSegmentNumber++;
        }

        int containingIdx = SegmentEditor.ContainingIndex(result.Segments, target);
        Guid? nextSelection = result.CreatedSegmentId
            ?? (containingIdx >= 0 ? result.Segments[containingIdx].Id : SelectedSegmentId);

        _logger.Info($"Point Out defini a {target:hh\\:mm\\:ss\\.fff}");
        Apply(result.Segments, nextSelection, clearMarkerSelection: true);
    }

    [RelayCommand(CanExecute = nameof(CanSetInOut))]
    private void SetIn()
    {
        (TimeSpan pos, _) = GetPlaybackContext();
        SetInAt(pos);
    }

    [RelayCommand(CanExecute = nameof(CanSetInOut))]
    private void SetOut()
    {
        (TimeSpan pos, _) = GetPlaybackContext();
        SetOutAt(pos);
    }

    private bool CanSetInOut() => HasMedia;

    [RelayCommand(CanExecute = nameof(CanDeleteMarker))]
    private void DeleteMarker()
    {
        if (SelectedMarkerIndex is not int idx || idx < 0 || idx >= Segments.Count - 1)
        {
            return;
        }

        TimeSpan boundaryTime = Segments[idx].End;
        SegmentEditResult result = SegmentEditor.MergeBoundary(Segments, idx);
        if (!result.Success)
        {
            ShowTransientStatus(result.ErrorMessage ?? "Suppression du marqueur impossible.");
            return;
        }

        _logger.Info($"Marqueur #{idx + 1} supprime a {boundaryTime:hh\\:mm\\:ss\\.fff} -> {result.Segments.Count} segment(s)");
        Apply(result.Segments, result.CreatedSegmentId, clearMarkerSelection: true);
    }

    private bool CanDeleteMarker()
        => HasMedia && SelectedMarkerIndex is int idx && idx >= 0 && idx < Segments.Count - 1;

    [RelayCommand(CanExecute = nameof(CanToggleEnabled))]
    private void ToggleEnabled()
    {
        if (SelectedSegmentId is not Guid id)
        {
            return;
        }

        int idx = FindSegmentIndex(Segments, id);
        if (idx < 0)
        {
            return;
        }

        SetSegmentEnabled(id, !Segments[idx].Enabled);
    }

    private bool CanToggleEnabled()
        => HasMedia && SelectedSegmentId is Guid id && FindSegmentIndex(Segments, id) >= 0;

    [RelayCommand(CanExecute = nameof(CanMergeWithPrevious))]
    private void MergeWithPrevious()
    {
        if (SelectedSegmentId is not Guid id)
        {
            return;
        }

        int idx = FindSegmentIndex(Segments, id);
        if (idx <= 0)
        {
            return;
        }

        TimeSpan boundaryTime = Segments[idx - 1].End;
        SegmentEditResult result = SegmentEditor.MergeBoundary(Segments, idx - 1);
        if (!result.Success)
        {
            ShowTransientStatus(result.ErrorMessage ?? "Fusion impossible.");
            return;
        }

        _logger.Info($"Fusion avec le segment precedent a {boundaryTime:hh\\:mm\\:ss\\.fff}");
        Apply(result.Segments, result.CreatedSegmentId, clearMarkerSelection: true);
    }

    private bool CanMergeWithPrevious()
        => HasMedia && SelectedSegmentId is Guid id && FindSegmentIndex(Segments, id) > 0;

    [RelayCommand(CanExecute = nameof(CanMergeWithNext))]
    private void MergeWithNext()
    {
        if (SelectedSegmentId is not Guid id)
        {
            return;
        }

        int idx = FindSegmentIndex(Segments, id);
        if (idx < 0 || idx >= Segments.Count - 1)
        {
            return;
        }

        TimeSpan boundaryTime = Segments[idx].End;
        SegmentEditResult result = SegmentEditor.MergeBoundary(Segments, idx);
        if (!result.Success)
        {
            ShowTransientStatus(result.ErrorMessage ?? "Fusion impossible.");
            return;
        }

        _logger.Info($"Fusion avec le segment suivant a {boundaryTime:hh\\:mm\\:ss\\.fff}");
        Apply(result.Segments, result.CreatedSegmentId, clearMarkerSelection: true);
    }

    private bool CanMergeWithNext()
        => HasMedia && SelectedSegmentId is Guid id
           && FindSegmentIndex(Segments, id) is int idx && idx >= 0 && idx < Segments.Count - 1;

    [RelayCommand(CanExecute = nameof(CanUndoExec))]
    private void Undo()
    {
        if (!_history.CanUndo)
        {
            return;
        }

        PendingSegmentStart = null;
        IReadOnlyList<Segment> restored = _history.Undo();
        Guid? nextSelection = ResolveSelectionAfterRestore(restored, SelectedSegmentId);

        _logger.Info($"Annulation effectuee ({restored.Count} segment(s))");
        ApplySnapshotWithoutHistory(restored, nextSelection, clearMarkerSelection: true);
    }

    private bool CanUndoExec() => CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedoExec))]
    private void Redo()
    {
        if (!_history.CanRedo)
        {
            return;
        }

        PendingSegmentStart = null;
        IReadOnlyList<Segment> restored = _history.Redo();
        Guid? nextSelection = ResolveSelectionAfterRestore(restored, SelectedSegmentId);

        _logger.Info($"Retablissement effectue ({restored.Count} segment(s))");
        ApplySnapshotWithoutHistory(restored, nextSelection, clearMarkerSelection: true);
    }

    private bool CanRedoExec() => CanRedo;

    [RelayCommand(CanExecute = nameof(CanNavigateKeyframes))]
    private void PreviousKeyframe()
    {
        (TimeSpan pos, _) = GetPlaybackContext();
        double? prev = KeyframeSnapper.Previous(pos.TotalSeconds, Keyframes);
        if (prev.HasValue)
        {
            _seekAction?.Invoke(TimeSpan.FromSeconds(prev.Value));
        }
    }

    [RelayCommand(CanExecute = nameof(CanNavigateKeyframes))]
    private void NextKeyframe()
    {
        (TimeSpan pos, _) = GetPlaybackContext();
        double? next = KeyframeSnapper.Next(pos.TotalSeconds, Keyframes);
        if (next.HasValue)
        {
            _seekAction?.Invoke(TimeSpan.FromSeconds(next.Value));
        }
    }

    private bool CanNavigateKeyframes() => HasMedia && Keyframes.Count > 0;

    [RelayCommand(CanExecute = nameof(CanNavigateMarkers))]
    private void PreviousMarker()
    {
        (TimeSpan pos, _) = GetPlaybackContext();
        double targetLimit = pos.TotalSeconds - MarkerNavigationEpsilonSeconds;

        for (int i = Segments.Count - 2; i >= 0; i--)
        {
            double boundary = Segments[i].End.TotalSeconds;
            if (boundary < targetLimit)
            {
                SelectedMarkerIndex = i;
                _seekAction?.Invoke(Segments[i].End);
                return;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanNavigateMarkers))]
    private void NextMarker()
    {
        (TimeSpan pos, _) = GetPlaybackContext();
        double targetLimit = pos.TotalSeconds + MarkerNavigationEpsilonSeconds;

        for (int i = 0; i < Segments.Count - 1; i++)
        {
            double boundary = Segments[i].End.TotalSeconds;
            if (boundary > targetLimit)
            {
                SelectedMarkerIndex = i;
                _seekAction?.Invoke(Segments[i].End);
                return;
            }
        }
    }

    private bool CanNavigateMarkers() => HasMedia && Segments.Count > 1;

    partial void OnSelectedSegmentIdChanged(Guid? value)
    {
        SyncSelectedRowFromId(value);
    }

    partial void OnSelectedSegmentRowChanged(SegmentRowViewModel? value)
    {
        if (_isSyncingSelection)
        {
            return;
        }

        SelectedMarkerIndex = null;
        SelectedSegmentId = value?.Id;

        if (value is not null)
        {
            _seekAction?.Invoke(value.Start);
        }
    }

    private void Apply(
        IReadOnlyList<Segment> newList,
        Guid? selection,
        bool clearMarkerSelection,
        string? statusMessage = null)
    {
        _history.Push(newList);
        ApplySnapshotWithoutHistory(newList, selection, clearMarkerSelection, statusMessage);
    }

    private void ApplySnapshotWithoutHistory(
        IReadOnlyList<Segment> snapshot,
        Guid? selection,
        bool clearMarkerSelection,
        string? statusMessage = null)
    {
        Segments = snapshot;

        if (clearMarkerSelection)
        {
            SelectedMarkerIndex = null;
        }
        else if (SelectedMarkerIndex is int idx && idx >= snapshot.Count - 1)
        {
            SelectedMarkerIndex = null;
        }

        SelectedSegmentId = ResolveSelectionAfterRestore(snapshot, selection);

        SyncSegmentRows();
        UpdateCountersAndCommandStates();

        if (!string.IsNullOrEmpty(statusMessage))
        {
            ShowTransientStatus(statusMessage);
        }
        else
        {
            ClearTransientStatus();
        }
    }

    private void SyncSegmentRows()
    {
        IReadOnlyList<Segment> segments = Segments;
        bool hasKeyframes = Keyframes.Count > 0;
        double fps = _currentMedia is { Fps: > 0 } ? _currentMedia.Fps : 30.0;
        double kfTolerance = 0.5 / fps;

        if (segments.Count == 0)
        {
            SegmentRows.Clear();
            SyncSelectedRowFromId(null);
            return;
        }

        var validIds = new HashSet<Guid>(segments.Count);
        for (int i = 0; i < segments.Count; i++)
        {
            validIds.Add(segments[i].Id);
        }

        for (int i = SegmentRows.Count - 1; i >= 0; i--)
        {
            if (!validIds.Contains(SegmentRows[i].Id))
            {
                SegmentRows.RemoveAt(i);
            }
        }

        for (int i = 0; i < segments.Count; i++)
        {
            Segment seg = segments[i];
            (string badge, string tooltip, bool onKf) = ComputeKeyframeBadge(seg, hasKeyframes, kfTolerance);

            int existingIndex = -1;
            for (int j = i; j < SegmentRows.Count; j++)
            {
                if (SegmentRows[j].Id == seg.Id)
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex == i)
            {
                SegmentRows[i].UpdateFrom(i + 1, seg, badge, tooltip, onKf);
            }
            else if (existingIndex > i)
            {
                SegmentRows.Move(existingIndex, i);
                SegmentRows[i].UpdateFrom(i + 1, seg, badge, tooltip, onKf);
            }
            else
            {
                var row = new SegmentRowViewModel(seg.Id, SetSegmentEnabled, RenameSegment);
                row.UpdateFrom(i + 1, seg, badge, tooltip, onKf);
                SegmentRows.Insert(i, row);
            }
        }

        SyncSelectedRowFromId(SelectedSegmentId);
    }

    private (string Badge, string Tooltip, bool IsOnKeyframe) ComputeKeyframeBadge(
        Segment segment,
        bool hasKeyframes,
        double toleranceSeconds)
    {
        if (!hasKeyframes)
        {
            return (string.Empty, string.Empty, false);
        }

        bool onKf = KeyframeSnapper.IsOnKeyframe(segment.Start.TotalSeconds, Keyframes, toleranceSeconds);
        return onKf
            ? ("✓", "Debut sur une keyframe : coupe rapide exacte", true)
            : ("⚠", "Hors keyframe : en mode Rapide, la coupe sera calee sur la keyframe precedente", false);
    }

    private void SyncSelectedRowFromId(Guid? segmentId)
    {
        SegmentRowViewModel? match = null;
        if (segmentId is Guid id)
        {
            for (int i = 0; i < SegmentRows.Count; i++)
            {
                if (SegmentRows[i].Id == id)
                {
                    match = SegmentRows[i];
                    break;
                }
            }
        }

        _isSyncingSelection = true;
        try
        {
            SelectedSegmentRow = match;
        }
        finally
        {
            _isSyncingSelection = false;
        }
    }

    private void RestoreRowName(Guid id, string modelName)
    {
        for (int i = 0; i < SegmentRows.Count; i++)
        {
            if (SegmentRows[i].Id == id)
            {
                SegmentRows[i].RestoreNameFromModel(modelName);
                break;
            }
        }
    }

    private void UpdateCountersAndCommandStates()
    {
        int count = Segments.Count;
        int includedCount = 0;
        long includedTicks = 0;

        for (int i = 0; i < count; i++)
        {
            Segment seg = Segments[i];
            if (seg.Enabled)
            {
                includedCount++;
                includedTicks += seg.Duration.Ticks;
            }
        }

        SegmentCount = count;
        IncludedSegmentCount = includedCount;
        IncludedDuration = TimeSpan.FromTicks(includedTicks);
        SummaryText = count == 0
            ? "Aucun segment"
            : $"{count} segment(s) · {includedCount} inclus ({FormatDuration(IncludedDuration)})";

        CanUndo = _history.CanUndo;
        CanRedo = _history.CanRedo;

        DeleteMarkerCommand.NotifyCanExecuteChanged();
        ToggleEnabledCommand.NotifyCanExecuteChanged();
        MergeWithPreviousCommand.NotifyCanExecuteChanged();
        MergeWithNextCommand.NotifyCanExecuteChanged();
        PreviousKeyframeCommand.NotifyCanExecuteChanged();
        NextKeyframeCommand.NotifyCanExecuteChanged();
        PreviousMarkerCommand.NotifyCanExecuteChanged();
        NextMarkerCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadKeyframesAsync(MediaInfo media, CancellationTokenSource cts)
    {
        try
        {
            IReadOnlyList<double> keyframes = await Task.Run(
                () => _ffmpegService.GetKeyframesAsync(media.Path, media, cts.Token),
                cts.Token);

            if (cts.IsCancellationRequested || _disposed || !ReferenceEquals(_keyframeCts, cts))
            {
                return;
            }

            Keyframes = keyframes;
            KeyframeStatus = keyframes.Count > 0
                ? $"{keyframes.Count} keyframes"
                : "Keyframes indisponibles";

            SyncSegmentRows();
            PreviousKeyframeCommand.NotifyCanExecuteChanged();
            NextKeyframeCommand.NotifyCanExecuteChanged();
        }
        catch (OperationCanceledException)
        {
            // Annulation normale lors du changement de video ou fermeture.
        }
        catch (Exception ex)
        {
            _logger.Warn($"Keyframes indisponibles pour {media.Path} : {ex.Message}");
            if (!cts.IsCancellationRequested && !_disposed && ReferenceEquals(_keyframeCts, cts))
            {
                Keyframes = Array.Empty<double>();
                KeyframeStatus = "Keyframes indisponibles";
                SyncSegmentRows();
                PreviousKeyframeCommand.NotifyCanExecuteChanged();
                NextKeyframeCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private void CancelKeyframeAnalysis()
    {
        _keyframeCts?.Cancel();
        _keyframeCts?.Dispose();
        _keyframeCts = null;
    }

    private (TimeSpan Position, double PixelsPerSecond) GetPlaybackContext()
    {
        return _playbackContextProvider is not null
            ? _playbackContextProvider.Invoke()
            : (TimeSpan.Zero, 10.0);
    }

    private void ShowTransientStatus(string message)
    {
        EditStatus = message;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void ClearTransientStatus()
    {
        _statusTimer.Stop();
        EditStatus = string.Empty;
    }

    private void OnStatusTimerTick(object? sender, EventArgs e)
    {
        _statusTimer.Stop();
        EditStatus = PendingSegmentStart is TimeSpan start
            ? BuildPendingStartStatus(start)
            : string.Empty;
    }

    private static string BuildPendingStartStatus(TimeSpan start)
    {
        int totalHours = (int)start.TotalHours;
        return $"Début : {totalHours:D2}:{start.Minutes:D2}:{start.Seconds:D2}.{start.Milliseconds:D3} — clic droit pour la fin (Échap pour annuler)";
    }

    private static int FindSegmentIndex(IReadOnlyList<Segment> segments, Guid id)
    {
        for (int i = 0; i < segments.Count; i++)
        {
            if (segments[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private static Guid? ResolveSelectionAfterRestore(IReadOnlyList<Segment> segments, Guid? preferred)
    {
        if (segments.Count == 0)
        {
            return null;
        }

        if (preferred is Guid id && FindSegmentIndex(segments, id) >= 0)
        {
            return id;
        }

        return segments[0].Id;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        int totalHours = (int)duration.TotalHours;
        return $"{totalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _statusTimer.Stop();
        _statusTimer.Tick -= OnStatusTimerTick;
        CancelKeyframeAnalysis();
    }
}
