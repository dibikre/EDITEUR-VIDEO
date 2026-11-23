using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using VideoSplitter.Models;
using VideoSplitter.Services;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace VideoSplitter.ViewModels;

public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    private const long ScrubThrottleMilliseconds = 50;

    public static readonly string[] SupportedVideoExtensions =
    [
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".ts", ".wmv", ".flv", ".mpg"
    ];

    private readonly VlcMediaPlayer _mediaPlayer;
    private readonly LibVLC _libVlc;
    private readonly IFfmpegService _ffmpegService;
    private readonly IThumbnailService _thumbnailService;
    private readonly IAppLogger _logger;
    private readonly Dispatcher _dispatcher;

    private CancellationTokenSource? _openCts;
    private CancellationTokenSource? _thumbnailCts;
    private Media? _currentMedia;
    private int _pauseOnFirstPlay;
    private long _pendingSeekTargetMs = -1;
    private long _lastScrubSeekTickMs;
    private bool _disposed;

    public IReadOnlyList<double> SpeedOptions { get; } = [0.5, 1.0, 1.5, 2.0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MediaSummary))]
    private MediaInfo? _mediaInfo;

    public string MediaSummary => MediaInfo is { } info
        ? $"{info.Width}x{info.Height} · {info.Fps:F2} fps · {info.VideoCodec}"
        : "Aucun fichier charge";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(
        nameof(PlayPauseCommand),
        nameof(StepForwardCommand),
        nameof(StepBackwardCommand),
        nameof(SkipForward5sCommand),
        nameof(SkipBackward5sCommand),
        nameof(GoToStartCommand),
        nameof(GoToEndCommand),
        nameof(ToggleMuteCommand))]
    private bool _isMediaLoaded;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private TimeSpan _position;

    [ObservableProperty]
    private TimeSpan _duration;

    [ObservableProperty]
    private int _volume = 100;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private double _speed = 1.0;

    [ObservableProperty]
    private IThumbnailProvider? _thumbnails;

    [ObservableProperty]
    private bool _isGeneratingThumbnails;

    [ObservableProperty]
    private double _thumbnailProgress;

    [ObservableProperty]
    private string _thumbnailStatus = "En attente";

    public event EventHandler<string>? ErrorOccurred;
    public Func<Task<bool>>? BeforeOpenConfirmAsync { get; set; }

    public PlayerViewModel(
        VlcMediaPlayer mediaPlayer,
        LibVLC libVlc,
        IFfmpegService ffmpegService,
        IThumbnailService thumbnailService,
        IAppLogger logger)
    {
        _mediaPlayer = mediaPlayer;
        _libVlc = libVlc;
        _ffmpegService = ffmpegService;
        _thumbnailService = thumbnailService;
        _logger = logger;
        _dispatcher = Application.Current.Dispatcher;

        AttachMediaPlayerEvents();
    }

    public static bool IsSupportedVideoFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string ext = System.IO.Path.GetExtension(path);
        return SupportedVideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    partial void OnVolumeChanged(int value)
    {
        int clamped = Math.Clamp(value, 0, 100);
        if (clamped != value)
        {
            Volume = clamped;
            return;
        }

        _mediaPlayer.Volume = clamped;
    }

    partial void OnIsMutedChanged(bool value)
    {
        _mediaPlayer.Mute = value;
    }

    partial void OnSpeedChanged(double value)
    {
        if (value > 0)
        {
            _mediaPlayer.SetRate((float)value);
        }
    }

    [RelayCommand]
    private async Task OuvrirAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Ouvrir une video",
            Filter = "Fichiers video (*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.ts;*.wmv;*.flv;*.mpg)|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.ts;*.wmv;*.flv;*.mpg|Tous les fichiers (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            await OpenAsync(dialog.FileName);
        }
    }

    public async Task OpenAsync(string path)
    {
        if (BeforeOpenConfirmAsync is not null)
        {
            bool shouldContinue = await BeforeOpenConfirmAsync.Invoke();
            if (!shouldContinue)
            {
                return;
            }
        }

        _openCts?.Cancel();
        _openCts?.Dispose();
        _openCts = new CancellationTokenSource();
        CancellationToken ct = _openCts.Token;

        CancelThumbnailGeneration();

        IsLoading = true;
        _logger.Info($"Ouverture demandee : {path}");

        try
        {
            MediaInfo info = await _ffmpegService.ProbeAsync(path, ct);
            ct.ThrowIfCancellationRequested();

            if (_mediaPlayer.IsPlaying || _mediaPlayer.State == VLCState.Paused)
            {
                await Task.Run(() => _mediaPlayer.Stop(), ct);
            }

            _currentMedia?.Dispose();
            _currentMedia = new Media(_libVlc, path, FromType.FromPath);

            MediaInfo = info;
            Duration = info.Duration;
            Position = TimeSpan.Zero;
            IsMediaLoaded = true;
            IsPlaying = false;

            _mediaPlayer.Volume = Volume;
            _mediaPlayer.Mute = IsMuted;
            _mediaPlayer.SetRate((float)Speed);

            Interlocked.Exchange(ref _pendingSeekTargetMs, -1);
            Interlocked.Exchange(ref _pauseOnFirstPlay, 1);

            if (!_mediaPlayer.Play(_currentMedia))
            {
                Interlocked.Exchange(ref _pauseOnFirstPlay, 0);
                throw new InvalidOperationException("LibVLC n'a pas pu demarrer la lecture du media.");
            }

            _ = StartThumbnailGenerationAsync(info);
        }
        catch (OperationCanceledException)
        {
            _logger.Info("Ouverture annulee.");
        }
        catch (MediaProbeException ex)
        {
            _logger.Error($"Echec d'ouverture ({path}) : {ex.UserMessage} | {ex.TechnicalDetails}", ex);
            ErrorOccurred?.Invoke(this, ex.UserMessage);
        }
        catch (Exception ex)
        {
            _logger.Error($"Erreur inattendue lors de l'ouverture de {path}", ex);
            ErrorOccurred?.Invoke(this, $"Impossible d'ouvrir la video.\n\nDetail : {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task StartThumbnailGenerationAsync(MediaInfo info)
    {
        var cts = new CancellationTokenSource();
        _thumbnailCts = cts;

        try
        {
            SpriteSet layout = await Task.Run(() => _thumbnailService.ComputeLayout(info), cts.Token);
            if (cts.IsCancellationRequested || _disposed)
            {
                return;
            }

            var store = new ThumbnailStore(layout, _thumbnailService, _logger);
            Thumbnails = store;
            IsGeneratingThumbnails = true;
            ThumbnailProgress = 0.0;
            ThumbnailStatus = "Vignettes : 0 %";

            var progress = new Progress<double>(fraction =>
            {
                if (cts.IsCancellationRequested || _disposed)
                {
                    return;
                }

                ThumbnailProgress = fraction;
                int percent = (int)Math.Round(fraction * 100.0);
                ThumbnailStatus = $"Vignettes : {percent} %";
            });

            await Task.Run(() => _thumbnailService.GetOrCreateAsync(info, progress, cts.Token), cts.Token);

            if (!cts.IsCancellationRequested && !_disposed)
            {
                ThumbnailProgress = 1.0;
                ThumbnailStatus = "Vignettes pretes";
            }
        }
        catch (OperationCanceledException)
        {
            // Annulation normale lors du changement de video ou fermeture.
        }
        catch (Exception ex)
        {
            _logger.Error($"Vignettes indisponibles pour {info.Path}", ex);
            if (!cts.IsCancellationRequested && !_disposed)
            {
                ThumbnailProgress = 0.0;
                ThumbnailStatus = "Vignettes indisponibles";
            }
        }
        finally
        {
            if (ReferenceEquals(_thumbnailCts, cts))
            {
                IsGeneratingThumbnails = false;
            }
        }
    }

    private void CancelThumbnailGeneration()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = null;

        Thumbnails?.Dispose();
        Thumbnails = null;
        IsGeneratingThumbnails = false;
        ThumbnailProgress = 0.0;
        ThumbnailStatus = "En attente";
    }

    [RelayCommand(CanExecute = nameof(IsMediaLoaded))]
    private void PlayPause()
    {
        if (!IsMediaLoaded)
        {
            return;
        }

        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.SetPause(true);
            return;
        }

        if (_mediaPlayer.State == VLCState.Ended)
        {
            Interlocked.Exchange(ref _pauseOnFirstPlay, 0);
            Interlocked.Exchange(ref _pendingSeekTargetMs, -1);
            _ = Task.Run(() =>
            {
                _mediaPlayer.Stop();
                _mediaPlayer.Play();
            });
            return;
        }

        _mediaPlayer.Play();
    }

    [RelayCommand(CanExecute = nameof(IsMediaLoaded))]
    private void StepForward()
    {
        if (!IsMediaLoaded)
        {
            return;
        }

        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.SetPause(true);
        }

        _mediaPlayer.NextFrame();
    }

    [RelayCommand(CanExecute = nameof(IsMediaLoaded))]
    private void StepBackward()
    {
        if (!IsMediaLoaded)
        {
            return;
        }

        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.SetPause(true);
        }

        double fps = MediaInfo is { Fps: > 0 } ? MediaInfo.Fps : 30.0;
        long stepMs = Math.Max(1L, (long)Math.Round(1000.0 / fps));
        long currentMs = _mediaPlayer.Time >= 0
            ? _mediaPlayer.Time
            : (long)Position.TotalMilliseconds;
        long targetMs = Math.Max(0L, currentMs - stepMs);

        SeekInternal(TimeSpan.FromMilliseconds(targetMs));
    }

    [RelayCommand(CanExecute = nameof(IsMediaLoaded))]
    private void SkipForward5s()
    {
        if (!IsMediaLoaded)
        {
            return;
        }

        SeekTo(Position + TimeSpan.FromSeconds(5));
    }

    [RelayCommand(CanExecute = nameof(IsMediaLoaded))]
    private void SkipBackward5s()
    {
        if (!IsMediaLoaded)
        {
            return;
        }

        SeekTo(Position - TimeSpan.FromSeconds(5));
    }

    [RelayCommand(CanExecute = nameof(IsMediaLoaded))]
    private void GoToStart()
    {
        if (IsMediaLoaded)
        {
            SeekTo(TimeSpan.Zero);
        }
    }

    [RelayCommand(CanExecute = nameof(IsMediaLoaded))]
    private void GoToEnd()
    {
        if (IsMediaLoaded)
        {
            SeekTo(Duration);
        }
    }

    [RelayCommand(CanExecute = nameof(IsMediaLoaded))]
    private void ToggleMute()
    {
        IsMuted = !IsMuted;
    }

    public void SeekTo(TimeSpan target)
    {
        if (!IsMediaLoaded)
        {
            return;
        }

        SeekInternal(target);
    }

    public void SeekFromBar(TimeSpan target, bool isFinal)
    {
        if (!IsMediaLoaded)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (!isFinal && now - _lastScrubSeekTickMs < ScrubThrottleMilliseconds)
        {
            return;
        }

        _lastScrubSeekTickMs = now;
        SeekInternal(target);
    }

    private void SeekInternal(TimeSpan target)
    {
        TimeSpan clamped = target < TimeSpan.Zero
            ? TimeSpan.Zero
            : (Duration > TimeSpan.Zero && target > Duration ? Duration : target);

        long targetMs = (long)clamped.TotalMilliseconds;
        Position = clamped;

        if (_mediaPlayer.State == VLCState.Ended)
        {
            Interlocked.Exchange(ref _pendingSeekTargetMs, targetMs);
            Interlocked.Exchange(ref _pauseOnFirstPlay, 1);
            _ = Task.Run(() =>
            {
                _mediaPlayer.Stop();
                _mediaPlayer.Play();
            });
            return;
        }

        _mediaPlayer.Time = targetMs;
    }

    private void AttachMediaPlayerEvents()
    {
        _mediaPlayer.TimeChanged += OnVlcTimeChanged;
        _mediaPlayer.LengthChanged += OnVlcLengthChanged;
        _mediaPlayer.Playing += OnVlcPlaying;
        _mediaPlayer.Paused += OnVlcPaused;
        _mediaPlayer.Stopped += OnVlcStopped;
        _mediaPlayer.EndReached += OnVlcEndReached;
        _mediaPlayer.EncounteredError += OnVlcEncounteredError;
    }

    private void DetachMediaPlayerEvents()
    {
        _mediaPlayer.TimeChanged -= OnVlcTimeChanged;
        _mediaPlayer.LengthChanged -= OnVlcLengthChanged;
        _mediaPlayer.Playing -= OnVlcPlaying;
        _mediaPlayer.Paused -= OnVlcPaused;
        _mediaPlayer.Stopped -= OnVlcStopped;
        _mediaPlayer.EndReached -= OnVlcEndReached;
        _mediaPlayer.EncounteredError -= OnVlcEncounteredError;
    }

    private void OnVlcTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        long ms = Math.Max(0L, e.Time);
        _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed && _mediaPlayer.State != VLCState.Ended)
            {
                Position = TimeSpan.FromMilliseconds(ms);
            }
        });
    }

    private void OnVlcLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        if (e.Length <= 0)
        {
            return;
        }

        long lengthMs = e.Length;
        _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed && Duration <= TimeSpan.Zero)
            {
                Duration = TimeSpan.FromMilliseconds(lengthMs);
            }
        });
    }

    private void OnVlcPlaying(object? sender, EventArgs e)
    {
        long seekTarget = Interlocked.Exchange(ref _pendingSeekTargetMs, -1);
        bool shouldPause = Interlocked.Exchange(ref _pauseOnFirstPlay, 0) == 1;

        if (shouldPause || seekTarget >= 0)
        {
            _ = Task.Run(() =>
            {
                if (seekTarget >= 0)
                {
                    _mediaPlayer.Time = seekTarget;
                }

                if (shouldPause)
                {
                    _mediaPlayer.SetPause(true);
                }
            });
        }

        _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed)
            {
                IsPlaying = !shouldPause;
            }
        });
    }

    private void OnVlcPaused(object? sender, EventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed)
            {
                IsPlaying = false;
            }
        });
    }

    private void OnVlcStopped(object? sender, EventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed)
            {
                IsPlaying = false;
            }
        });
    }

    private void OnVlcEndReached(object? sender, EventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed)
            {
                IsPlaying = false;
                Position = Duration;
            }
        });
    }

    private void OnVlcEncounteredError(object? sender, EventArgs e)
    {
        _logger.Error("LibVLC a rencontre une erreur de lecture.");
        _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed)
            {
                IsPlaying = false;
                ErrorOccurred?.Invoke(this, "Une erreur est survenue pendant la lecture de la video.");
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DetachMediaPlayerEvents();

        _openCts?.Cancel();
        _openCts?.Dispose();
        _openCts = null;

        CancelThumbnailGeneration();

        _currentMedia?.Dispose();
        _currentMedia = null;
    }
}
