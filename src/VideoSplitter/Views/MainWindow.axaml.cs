using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VideoSplitter.Models;
using VideoSplitter.Services;
using VideoSplitter.ViewModels;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace VideoSplitter.Views;

public partial class MainWindow : Window
{
    private static readonly FilePickerFileType VideoFileType = new("Fichiers vidéo")
    {
        Patterns = ["*.mp4", "*.mkv", "*.mov", "*.avi", "*.webm", "*.m4v", "*.ts", "*.wmv", "*.flv", "*.mpg"]
    };

    private readonly MainViewModel _viewModel;
    private readonly VlcMediaPlayer _mediaPlayer;
    private readonly IAppLogger _logger;
    private SegmentRowViewModel? _mouseDownAlreadySelectedRow;
    private bool _isClosingAfterExportCancel;
    private bool _isClosingPromptOpen;

    public MainWindow(MainViewModel viewModel, VlcMediaPlayer mediaPlayer, IAppLogger logger)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = _viewModel;
        _mediaPlayer = mediaPlayer;
        _logger = logger;

        ConfigureBridges();

        Opened += OnWindowOpened;
        Closed += OnWindowClosed;
        _viewModel.Player.ErrorOccurred += OnPlayerErrorOccurred;

        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DropEvent, OnWindowDrop, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnWindowTunnelKeyDown, RoutingStrategies.Tunnel);

        SegmentsListBox.AddHandler(PointerPressedEvent, OnSegmentsListPointerPressed, RoutingStrategies.Tunnel);
        SegmentsListBox.AddHandler(PointerReleasedEvent, OnSegmentsListPointerReleased, RoutingStrategies.Tunnel);
    }

    private void ConfigureBridges()
    {
        _viewModel.Player.PickVideoFileAsync = async () =>
        {
            IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "Ouvrir une vidéo",
                    AllowMultiple = false,
                    FileTypeFilter = [VideoFileType, FilePickerFileTypes.All]
                });

            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        };

        _viewModel.ConfirmAbortExportForOpenAsync = () =>
            ShowDialogMessageAsync(
                "VideoSplitter — Confirmation",
                "Un export est en cours. L'annuler et ouvrir la nouvelle vidéo ?",
                isConfirmation: true);

        _viewModel.Export.ConfigureBridges(
            sourceProvider: () => (_viewModel.Player.MediaInfo, _viewModel.Edit.Segments, _viewModel.Edit.Keyframes),
            fastCutWarningPrompt: async (outOfKeyframeCount, keyframesUnavailable) =>
            {
                var dialog = new FastCutWarningDialog(outOfKeyframeCount, keyframesUnavailable);
                return await dialog.ShowDialog<FastCutWarningChoice>(this);
            },
            conflictPrompt: async conflictingPaths =>
            {
                var dialog = new ExportConflictDialog(conflictingPaths);
                return await dialog.ShowDialog<ConflictPolicy?>(this);
            },
            confirmContinuePrompt: message =>
                ShowDialogMessageAsync("VideoSplitter — Avertissement", message, isConfirmation: true),
            errorPrompt: message =>
                ShowDialogMessageAsync("VideoSplitter — Erreur d'export", message, isConfirmation: false),
            pickFolderPrompt: async initialDirectory =>
            {
                IStorageFolder? suggestedLocation = null;
                if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
                {
                    suggestedLocation = await StorageProvider.TryGetFolderFromPathAsync(initialDirectory);
                }

                IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
                    new FolderPickerOpenOptions
                    {
                        Title = "Choisir le dossier de sortie",
                        AllowMultiple = false,
                        SuggestedStartLocation = suggestedLocation
                    });

                return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
            },
            copyTextAction: async text =>
            {
                if (Clipboard is not null)
                {
                    await Clipboard.SetTextAsync(text);
                }
            });
    }

    private Task<bool> ShowDialogMessageAsync(string title, string message, bool isConfirmation)
    {
        var dialog = new MessageDialog(title, message, isConfirmation);
        return dialog.ShowDialog<bool>(this);
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        if (LecteurVideoView.MediaPlayer is null)
        {
            LecteurVideoView.MediaPlayer = _mediaPlayer;
            _logger.Info("MediaPlayer LibVLC associe a VideoView (Avalonia).");
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (_isClosingAfterExportCancel || !_viewModel.Export.IsExporting)
        {
            return;
        }

        e.Cancel = true;
        if (_isClosingPromptOpen)
        {
            return;
        }

        _isClosingPromptOpen = true;
        _ = ConfirmCancelExportAndCloseAsync();
    }

    private async Task ConfirmCancelExportAndCloseAsync()
    {
        try
        {
            bool shouldQuit = await ShowDialogMessageAsync(
                "VideoSplitter — Confirmation",
                "Un export est en cours. L'annuler et quitter l'application ?",
                isConfirmation: true);

            if (shouldQuit)
            {
                await _viewModel.Export.CancelAndWaitAsync(TimeSpan.FromSeconds(5));
                _isClosingAfterExportCancel = true;
                Close();
            }
        }
        finally
        {
            _isClosingPromptOpen = false;
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        Opened -= OnWindowOpened;
        Closed -= OnWindowClosed;
        _viewModel.Player.ErrorOccurred -= OnPlayerErrorOccurred;
        _viewModel.Player.PickVideoFileAsync = null;
        _viewModel.ConfirmAbortExportForOpenAsync = null;

        RemoveHandler(DragDrop.DragOverEvent, OnWindowDragOver);
        RemoveHandler(DragDrop.DropEvent, OnWindowDrop);
        RemoveHandler(KeyDownEvent, OnWindowTunnelKeyDown);

        SegmentsListBox.RemoveHandler(PointerPressedEvent, OnSegmentsListPointerPressed);
        SegmentsListBox.RemoveHandler(PointerReleasedEvent, OnSegmentsListPointerReleased);

        // Detacher le lecteur avant la liberation du conteneur DI dans App.OnDesktopExit.
        LecteurVideoView.MediaPlayer = null;
        _logger.Info("Fenetre principale fermee.");
    }

    private void OnPlayerErrorOccurred(object? sender, string message)
    {
        _ = ShowDialogMessageAsync("VideoSplitter — Erreur", message, isConfirmation: false);
    }

    private void OnSeekBarSeekRequested(object? sender, TimeSpan target)
    {
        _viewModel.Player.SeekFromBar(target, isFinal: false);
    }

    private void OnSeekBarSeekCompleted(object? sender, TimeSpan target)
    {
        _viewModel.Player.SeekFromBar(target, isFinal: true);
    }

    private void OnTimelineSegmentSelectionRequested(object? sender, Guid? segmentId)
    {
        _viewModel.Edit.SelectSegment(segmentId);
    }

    private void OnTimelineMarkerSelectionRequested(object? sender, int? markerIndex)
    {
        _viewModel.Edit.SelectMarker(markerIndex);
    }

    private void OnTimelineBoundaryDragPreview(object? sender, (int Index, TimeSpan Time) e)
    {
        _viewModel.Player.SeekFromBar(e.Time, isFinal: false);
    }

    private void OnTimelineBoundaryDragCompleted(object? sender, (int Index, TimeSpan Time, bool Cancelled) e)
    {
        if (e.Cancelled)
        {
            return;
        }

        _viewModel.Edit.ApplyBoundaryMove(e.Index, e.Time);
        _viewModel.Player.SeekFromBar(e.Time, isFinal: true);
    }

    private void OnSegmentsListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _mouseDownAlreadySelectedRow = null;
        if (!e.GetCurrentPoint(SegmentsListBox).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is not Visual source)
        {
            return;
        }

        if (source.FindAncestorOfType<TextBox>(includeSelf: true) is not null
            || source.FindAncestorOfType<CheckBox>(includeSelf: true) is not null)
        {
            return;
        }

        ListBoxItem? item = source.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (item is { IsSelected: true, DataContext: SegmentRowViewModel row })
        {
            _mouseDownAlreadySelectedRow = row;
        }
    }

    private void OnSegmentsListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        SegmentRowViewModel? targetRow = _mouseDownAlreadySelectedRow;
        _mouseDownAlreadySelectedRow = null;

        if (targetRow is null || e.InitialPressMouseButton != MouseButton.Left || e.Source is not Visual source)
        {
            return;
        }

        if (source.FindAncestorOfType<TextBox>(includeSelf: true) is not null
            || source.FindAncestorOfType<CheckBox>(includeSelf: true) is not null)
        {
            return;
        }

        ListBoxItem? item = source.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (item is { DataContext: SegmentRowViewModel releasedRow } && ReferenceEquals(releasedRow, targetRow))
        {
            _viewModel.Edit.SeekToSegmentStart(releasedRow.Id);
        }
    }

    private void OnSegmentNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            FocusManager?.ClearFocus();
            Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (textBox.DataContext is SegmentRowViewModel row)
            {
                textBox.Text = row.Name;
            }

            FocusManager?.ClearFocus();
            Focus();
            e.Handled = true;
        }
    }

    private void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File))
        {
            return;
        }

        e.DragEffects = TryGetDroppedVideoPath(e, out _)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWindowDrop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File))
        {
            return;
        }

        e.Handled = true;
        if (TryGetDroppedVideoPath(e, out string? path) && path is not null)
        {
            Dispatcher.UIThread.Post(() => _ = OpenDroppedVideoAsync(path));
        }
    }

    private async Task OpenDroppedVideoAsync(string path)
    {
        Activate();
        Focus();
        await _viewModel.Player.OpenAsync(path);
    }

    private static bool TryGetDroppedVideoPath(DragEventArgs e, out string? videoPath)
    {
        videoPath = null;
        if (!e.DataTransfer.Contains(DataFormat.File))
        {
            return false;
        }

        IEnumerable<IStorageItem>? items = e.DataTransfer.TryGetFiles();
        if (items is null)
        {
            return false;
        }

        foreach (IStorageItem item in items)
        {
            string? candidate = item.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(candidate)
                && File.Exists(candidate)
                && PlayerViewModel.IsSupportedVideoFile(candidate))
            {
                videoPath = candidate;
                return true;
            }

            break;
        }

        return false;
    }

    private void OnWindowTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsTextInputFocused())
        {
            return;
        }

        if (e.Key == Key.Escape && MainTimelineView.CancelActiveDrag())
        {
            e.Handled = true;
            return;
        }

        if (!_viewModel.Player.IsMediaLoaded)
        {
            return;
        }

        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.Z when shift:
                    _viewModel.Edit.RedoCommand.Execute(null);
                    e.Handled = true;
                    break;

                case Key.Z:
                    _viewModel.Edit.UndoCommand.Execute(null);
                    e.Handled = true;
                    break;

                case Key.Y:
                    _viewModel.Edit.RedoCommand.Execute(null);
                    e.Handled = true;
                    break;

                case Key.Left:
                    _viewModel.Edit.PreviousKeyframeCommand.Execute(null);
                    e.Handled = true;
                    break;

                case Key.Right:
                    _viewModel.Edit.NextKeyframeCommand.Execute(null);
                    e.Handled = true;
                    break;

                case Key.OemPlus:
                case Key.Add:
                    _viewModel.Timeline.ZoomInCommand.Execute(null);
                    e.Handled = true;
                    break;

                case Key.OemMinus:
                case Key.Subtract:
                    _viewModel.Timeline.ZoomOutCommand.Execute(null);
                    e.Handled = true;
                    break;

                case Key.D0:
                case Key.NumPad0:
                    _viewModel.Timeline.FitToWindowCommand.Execute(null);
                    e.Handled = true;
                    break;
            }

            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                _viewModel.Player.PlayPauseCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.S:
                _viewModel.Edit.SplitCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.I:
                _viewModel.Edit.SetInCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.O:
                _viewModel.Edit.SetOutCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.E:
                _viewModel.Edit.ToggleEnabledCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Delete:
                _viewModel.Edit.DeleteMarkerCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.PageUp:
                _viewModel.Edit.PreviousMarkerCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.PageDown:
                _viewModel.Edit.NextMarkerCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Left when shift:
                _viewModel.Player.SkipBackward5sCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Right when shift:
                _viewModel.Player.SkipForward5sCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Left:
                _viewModel.Player.StepBackwardCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Right:
                _viewModel.Player.StepForwardCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Home:
                _viewModel.Player.GoToStartCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.End:
                _viewModel.Player.GoToEndCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private bool IsTextInputFocused()
    {
        if (FocusManager?.GetFocusedElement() is Visual visual)
        {
            return visual.FindAncestorOfType<TextBox>(includeSelf: true) is not null;
        }

        return false;
    }
}
