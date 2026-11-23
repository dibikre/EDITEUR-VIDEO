using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using VideoSplitter.Services;
using VideoSplitter.ViewModels;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace VideoSplitter.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly VlcMediaPlayer _mediaPlayer;
    private readonly IAppLogger _logger;
    private SegmentRowViewModel? _mouseDownAlreadySelectedRow;

    public MainWindow(MainViewModel viewModel, VlcMediaPlayer mediaPlayer, IAppLogger logger)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = _viewModel;
        _mediaPlayer = mediaPlayer;
        _logger = logger;

        Loaded += OnLoaded;
        Closed += OnClosed;
        _viewModel.Player.ErrorOccurred += OnPlayerErrorOccurred;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (LecteurVideoView.MediaPlayer is null)
        {
            LecteurVideoView.MediaPlayer = _mediaPlayer;
            _logger.Info("MediaPlayer LibVLC associe a VideoView.");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Loaded -= OnLoaded;
        Closed -= OnClosed;
        _viewModel.Player.ErrorOccurred -= OnPlayerErrorOccurred;

        // Detacher le lecteur avant la liberation du conteneur DI dans App.OnExit.
        LecteurVideoView.MediaPlayer = null;
        _logger.Info("Fenetre principale fermee.");
    }

    private void OnPlayerErrorOccurred(object? sender, string message)
    {
        MessageBox.Show(
            this,
            message,
            "VideoSplitter — Erreur",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
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

    private void OnSegmentsListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mouseDownAlreadySelectedRow = null;
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (FindAncestor<TextBoxBase>(source) is not null || FindAncestor<CheckBox>(source) is not null)
        {
            return;
        }

        ListViewItem? item = FindAncestor<ListViewItem>(source);
        if (item is { IsSelected: true, DataContext: SegmentRowViewModel row })
        {
            _mouseDownAlreadySelectedRow = row;
        }
    }

    private void OnSegmentsListPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        SegmentRowViewModel? targetRow = _mouseDownAlreadySelectedRow;
        _mouseDownAlreadySelectedRow = null;

        if (targetRow is null || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (FindAncestor<TextBoxBase>(source) is not null || FindAncestor<CheckBox>(source) is not null)
        {
            return;
        }

        ListViewItem? item = FindAncestor<ListViewItem>(source);
        if (item is { DataContext: SegmentRowViewModel releasedRow } && ReferenceEquals(releasedRow, targetRow))
        {
            _viewModel.Edit.SeekToSegmentStart(releasedRow.Id);
        }
    }

    private void OnSegmentNamePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            BindingExpression? binding = textBox.GetBindingExpression(TextBox.TextProperty);
            binding?.UpdateSource();
            Keyboard.ClearFocus();
            Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            BindingExpression? binding = textBox.GetBindingExpression(TextBox.TextProperty);
            binding?.UpdateTarget();
            Keyboard.ClearFocus();
            Focus();
            e.Handled = true;
        }
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        DependencyObject? node = current;
        while (node is not null)
        {
            if (node is T match)
            {
                return match;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        if (TryGetDroppedVideoPath(e, out _))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (TryGetDroppedVideoPath(e, out string? path) && path is not null)
        {
            await _viewModel.Player.OpenAsync(path);
        }
    }

    private static bool TryGetDroppedVideoPath(DragEventArgs e, out string? videoPath)
    {
        videoPath = null;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return false;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return false;
        }

        string candidate = files[0];
        if (!PlayerViewModel.IsSupportedVideoFile(candidate))
        {
            return false;
        }

        videoPath = candidate;
        return true;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
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

        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
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

    private static bool IsTextInputFocused()
    {
        return Keyboard.FocusedElement switch
        {
            TextBoxBase => true,
            PasswordBox => true,
            ComboBox { IsEditable: true } => true,
            _ => false
        };
    }
}
