using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using VideoSplitter.Services;
using VideoSplitter.ViewModels;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace VideoSplitter.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly VlcMediaPlayer _mediaPlayer;
    private readonly IAppLogger _logger;

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

        if (!_viewModel.Player.IsMediaLoaded)
        {
            return;
        }

        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (ctrl)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                _viewModel.Player.PlayPauseCommand.Execute(null);
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
