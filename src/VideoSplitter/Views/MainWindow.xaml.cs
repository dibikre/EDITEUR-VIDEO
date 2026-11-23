using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VideoSplitter.Models;
using VideoSplitter.Services;
using VideoSplitter.ViewModels;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace VideoSplitter.Views;

public partial class MainWindow : Window
{
    private const int WmEraseBkgnd = 0x0014;
    private const int WmCtlColorStatic = 0x0138;
    private const int WmDropFiles = 0x0233;
    private const uint VideoBackgroundColorRef = 0x000C0A09u; // #090A0C en BGR

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("gdi32.dll", SetLastError = false)]
    private static extern nint CreateSolidBrush(uint colorRef);

    [DllImport("gdi32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint hObject);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hWnd, out Win32Rect lpRect);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern int FillRect(nint hDc, ref Win32Rect lprc, nint hbr);

    [DllImport("shell32.dll", SetLastError = false)]
    private static extern void DragAcceptFiles(nint hWnd, [MarshalAs(UnmanagedType.Bool)] bool fAccept);

    [DllImport("shell32.dll", EntryPoint = "DragQueryFileW", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern uint DragQueryFile(nint hDrop, uint iFile, [Out] char[]? lpszFile, uint cch);

    [DllImport("shell32.dll", SetLastError = false)]
    private static extern void DragFinish(nint hDrop);

    private readonly MainViewModel _viewModel;
    private readonly VlcMediaPlayer _mediaPlayer;
    private readonly IAppLogger _logger;
    private SegmentRowViewModel? _mouseDownAlreadySelectedRow;
    private bool _isClosingAfterExportCancel;
    private HwndSource? _windowHwndSource;
    private HwndHost? _videoHwndHost;
    private nint _videoBgBrush;

    public MainWindow(MainViewModel viewModel, VlcMediaPlayer mediaPlayer, IAppLogger logger)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = _viewModel;
        _mediaPlayer = mediaPlayer;
        _logger = logger;

        ConfigureExportBridges();

        Loaded += OnLoaded;
        Closed += OnClosed;
        _viewModel.Player.ErrorOccurred += OnPlayerErrorOccurred;
    }

    private void ConfigureExportBridges()
    {
        _viewModel.ConfirmAbortExportForOpen = () =>
        {
            MessageBoxResult answer = MessageBox.Show(
                this,
                "Un export est en cours. L'annuler et ouvrir la nouvelle vidéo ?",
                "VideoSplitter — Confirmation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            return answer == MessageBoxResult.Yes;
        };

        _viewModel.Export.ConfigureBridges(
            sourceProvider: () => (_viewModel.Player.MediaInfo, _viewModel.Edit.Segments, _viewModel.Edit.Keyframes),
            fastCutWarningPrompt: (outOfKeyframeCount, keyframesUnavailable) =>
            {
                var dialog = new FastCutWarningDialog(outOfKeyframeCount, keyframesUnavailable)
                {
                    Owner = this
                };
                dialog.ShowDialog();
                return dialog.Choice;
            },
            conflictPrompt: conflictingPaths =>
            {
                var dialog = new ExportConflictDialog(conflictingPaths)
                {
                    Owner = this
                };
                dialog.ShowDialog();
                return dialog.SelectedPolicy;
            },
            confirmContinuePrompt: message =>
            {
                MessageBoxResult answer = MessageBox.Show(
                    this,
                    message,
                    "VideoSplitter — Avertissement",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                return answer == MessageBoxResult.Yes;
            },
            errorPrompt: message =>
            {
                MessageBox.Show(
                    this,
                    message,
                    "VideoSplitter — Erreur d'export",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            });
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (LecteurVideoView.MediaPlayer is null)
        {
            LecteurVideoView.MediaPlayer = _mediaPlayer;
            _logger.Info("MediaPlayer LibVLC associe a VideoView.");
        }

        if (_videoBgBrush == 0)
        {
            _videoBgBrush = CreateSolidBrush(VideoBackgroundColorRef);
        }

        if (_windowHwndSource is null && PresentationSource.FromVisual(this) is HwndSource source)
        {
            _windowHwndSource = source;
            _windowHwndSource.AddHook(OnWindowHwndMessage);
        }

        LecteurVideoView.IsVisibleChanged += OnVideoViewIsVisibleChanged;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, AttachVideoHostHook);
    }

    private void OnVideoViewIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && _videoHwndHost is null)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, AttachVideoHostHook);
        }
    }

    private void AttachVideoHostHook()
    {
        if (_videoHwndHost is not null)
        {
            return;
        }

        HwndHost? host = FindDescendant<HwndHost>(LecteurVideoView);
        if (host is null)
        {
            return;
        }

        _videoHwndHost = host;
        _videoHwndHost.MessageHook += OnVideoHostMessage;
        if (_videoHwndHost.Handle != IntPtr.Zero)
        {
            DragAcceptFiles(_videoHwndHost.Handle, true);
        }
    }

    private nint OnWindowHwndMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmCtlColorStatic
            && _videoBgBrush != 0
            && _videoHwndHost is { Handle: var hostHandle }
            && hostHandle != IntPtr.Zero
            && lParam == hostHandle)
        {
            handled = true;
            return _videoBgBrush;
        }

        return IntPtr.Zero;
    }

    private nint OnVideoHostMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmEraseBkgnd && _videoBgBrush != 0 && wParam != IntPtr.Zero)
        {
            if (GetClientRect(hwnd, out Win32Rect rect))
            {
                FillRect(wParam, ref rect, _videoBgBrush);
                handled = true;
                return new IntPtr(1);
            }
        }
        else if (msg == WmDropFiles && wParam != IntPtr.Zero)
        {
            string? droppedPath = TryReadFirstDroppedFile(wParam);
            DragFinish(wParam);
            if (droppedPath is not null
                && File.Exists(droppedPath)
                && PlayerViewModel.IsSupportedVideoFile(droppedPath))
            {
                handled = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => _ = OpenDroppedVideoAsync(droppedPath));
            }
        }

        return IntPtr.Zero;
    }

    private static string? TryReadFirstDroppedFile(nint hDrop)
    {
        uint count = DragQueryFile(hDrop, uint.MaxValue, null, 0);
        if (count == 0)
        {
            return null;
        }

        uint len = DragQueryFile(hDrop, 0, null, 0);
        if (len == 0)
        {
            return null;
        }

        var buffer = new char[len + 1];
        uint copied = DragQueryFile(hDrop, 0, buffer, (uint)buffer.Length);
        return copied > 0 ? new string(buffer, 0, (int)copied) : null;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_isClosingAfterExportCancel || !_viewModel.Export.IsExporting)
        {
            return;
        }

        MessageBoxResult answer = MessageBox.Show(
            this,
            "Un export est en cours. L'annuler et quitter l'application ?",
            "VideoSplitter — Confirmation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        e.Cancel = true;
        if (answer == MessageBoxResult.Yes)
        {
            _ = CancelExportAndCloseAsync();
        }
    }

    private async Task CancelExportAndCloseAsync()
    {
        await _viewModel.Export.CancelAndWaitAsync(TimeSpan.FromSeconds(5));
        _isClosingAfterExportCancel = true;
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Loaded -= OnLoaded;
        Closed -= OnClosed;
        LecteurVideoView.IsVisibleChanged -= OnVideoViewIsVisibleChanged;
        _viewModel.Player.ErrorOccurred -= OnPlayerErrorOccurred;
        _viewModel.ConfirmAbortExportForOpen = null;

        if (_videoHwndHost is not null)
        {
            _videoHwndHost.MessageHook -= OnVideoHostMessage;
            _videoHwndHost = null;
        }

        if (_windowHwndSource is not null)
        {
            _windowHwndSource.RemoveHook(OnWindowHwndMessage);
            _windowHwndSource = null;
        }

        if (_videoBgBrush != 0)
        {
            DeleteObject(_videoBgBrush);
            _videoBgBrush = 0;
        }

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

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            T? nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void OnWindowPreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        e.Effects = TryGetDroppedVideoPath(e, out _)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWindowPreviewDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        e.Handled = true;
        if (TryGetDroppedVideoPath(e, out string? path) && path is not null)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => _ = OpenDroppedVideoAsync(path));
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
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return false;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return false;
        }

        string candidate = files[0];
        if (!File.Exists(candidate) || !PlayerViewModel.IsSupportedVideoFile(candidate))
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
