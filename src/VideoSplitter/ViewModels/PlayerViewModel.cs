using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
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

    private CancellationTokenSource? _openCts;
    private CancellationTokenSource? _thumbnailCts;
    private Media? _currentMedia;
    private int _pauseOnFirstPlay;
    private int _hasVoutForCurrentOpen;
    private long _openGeneration;
    private long _pendingSeekTargetMs = -1;
    private long _lastScrubSeekTickMs;
    private bool _disposed;

    public IReadOnlyList<double> SpeedOptions { get; } = [0.5, 1.0, 1.5, 2.0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MediaSummary))]
    [NotifyPropertyChangedFor(nameof(IsPortraitVideo))]
    private MediaInfo? _mediaInfo;

    public bool IsPortraitVideo => MediaInfo is { Width: > 0, Height: > 0 } info && info.Width < info.Height;

    public string MediaSummary => MediaInfo is { } info
        ? $"{info.Width}x{info.Height} · {info.Fps:F2} fps · {info.VideoCodec}"
        : "Aucun fichier chargé";

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
    public Func<Task<string?>>? PickVideoFileAsync { get; set; }

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

        AttachMediaPlayerEvents();
    }

    public static bool IsSupportedVideoFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string ext = Path.GetExtension(path);
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
        if (PickVideoFileAsync is null)
        {
            return;
        }

        string? filePath = await PickVideoFileAsync.Invoke();
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            await OpenAsync(filePath);
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

        long generation = Interlocked.Increment(ref _openGeneration);

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
            _mediaPlayer.SetRate((float)Speed);

            Interlocked.Exchange(ref _pendingSeekTargetMs, -1);
            Interlocked.Exchange(ref _hasVoutForCurrentOpen, 0);
            Interlocked.Exchange(ref _pauseOnFirstPlay, 1);

            // Couper temporairement le son pendant le pre-roll de la premiere image
            _mediaPlayer.Mute = true;

            if (!_mediaPlayer.Play(_currentMedia))
            {
                Interlocked.Exchange(ref _pauseOnFirstPlay, 0);
                _mediaPlayer.Mute = IsMuted;
                throw new InvalidOperationException("LibVLC n'a pas pu demarrer la lecture du media.");
            }

            ScheduleInitialPauseFallback(generation, 1500);
            _ = StartThumbnailGenerationAsync(info);
        }
        catch (OperationCanceledException)
        {
            if (Volatile.Read(ref _openGeneration) == generation)
            {
                Interlocked.Exchange(ref _pauseOnFirstPlay, 0);
                _mediaPlayer.Mute = IsMuted;
            }

            _logger.Info("Ouverture annulee.");
        }
        catch (MediaProbeException ex)
        {
            Interlocked.Exchange(ref _pauseOnFirstPlay, 0);
            _mediaPlayer.Mute = IsMuted;
            _logger.Error($"Echec d'ouverture ({path}) : {ex.UserMessage} | {ex.TechnicalDetails}", ex);
            ErrorOccurred?.Invoke(this, ex.UserMessage);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _pauseOnFirstPlay, 0);
            _mediaPlayer.Mute = IsMuted;
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

        if (Interlocked.Exchange(ref _pauseOnFirstPlay, 0) == 1)
        {
            _mediaPlayer.Mute = IsMuted;
            if (_mediaPlayer.State == VLCState.Paused)
            {
                _mediaPlayer.Play();
            }

            IsPlaying = true;
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
            _mediaPlayer.Mute = IsMuted;
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
            long generation = Interlocked.Increment(ref _openGeneration);
            Interlocked.Exchange(ref _pendingSeekTargetMs, targetMs);
            Interlocked.Exchange(ref _hasVoutForCurrentOpen, 0);
            Interlocked.Exchange(ref _pauseOnFirstPlay, 1);
            _mediaPlayer.Mute = true;
            _ = Task.Run(() =>
            {
                _mediaPlayer.Stop();
                _mediaPlayer.Play();
            });
            ScheduleInitialPauseFallback(generation, 1000);
            return;
        }

        _mediaPlayer.Time = targetMs;
    }

    private void AttachMediaPlayerEvents()
    {
        _mediaPlayer.TimeChanged += OnVlcTimeChanged;
        _mediaPlayer.LengthChanged += OnVlcLengthChanged;
        _mediaPlayer.Vout += OnVlcVout;
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
        _mediaPlayer.Vout -= OnVlcVout;
        _mediaPlayer.Playing -= OnVlcPlaying;
        _mediaPlayer.Paused -= OnVlcPaused;
        _mediaPlayer.Stopped -= OnVlcStopped;
        _mediaPlayer.EndReached -= OnVlcEndReached;
        _mediaPlayer.EncounteredError -= OnVlcEncounteredError;
    }

    private void OnVlcTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        long ms = Math.Max(0L, e.Time);

        if (Volatile.Read(ref _pauseOnFirstPlay) == 1
            && Volatile.Read(ref _hasVoutForCurrentOpen) == 1
            && ms > 0)
        {
            CompleteInitialPause(Volatile.Read(ref _openGeneration));
            return;
        }

        if (Volatile.Read(ref _pauseOnFirstPlay) == 1)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
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
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed && Duration <= TimeSpan.Zero)
            {
                Duration = TimeSpan.FromMilliseconds(lengthMs);
            }
        });
    }

    private void OnVlcVout(object? sender, MediaPlayerVoutEventArgs e)
    {
        if (e.Count <= 0 || Volatile.Read(ref _pauseOnFirstPlay) != 1)
        {
            return;
        }

        Interlocked.Exchange(ref _hasVoutForCurrentOpen, 1);
        long gen = Volatile.Read(ref _openGeneration);

        long seekTarget = Volatile.Read(ref _pendingSeekTargetMs);
        if (seekTarget >= 0)
        {
            CompleteInitialPause(gen);
            return;
        }

        ScheduleInitialPauseFallback(gen, 280);
    }

    private void ScheduleInitialPauseFallback(long generation, int delayMs)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
            if (!_disposed
                && Volatile.Read(ref _openGeneration) == generation
                && Volatile.Read(ref _pauseOnFirstPlay) == 1)
            {
                CompleteInitialPause(generation);
            }
        });
    }

    private void CompleteInitialPause(long generation)
    {
        if (_disposed || Volatile.Read(ref _openGeneration) != generation)
        {
            return;
        }

        if (Interlocked.Exchange(ref _pauseOnFirstPlay, 0) != 1)
        {
            return;
        }

        long seekTarget = Interlocked.Exchange(ref _pendingSeekTargetMs, -1);
        bool targetMute = IsMuted;
        _ = Task.Run(() =>
        {
            if (_disposed || Volatile.Read(ref _openGeneration) != generation)
            {
                return;
            }

            if (seekTarget >= 0)
            {
                _mediaPlayer.Time = seekTarget;
            }

            _mediaPlayer.SetPause(true);
            _mediaPlayer.Mute = targetMute;
        });

        TimeSpan uiPos = seekTarget >= 0 ? TimeSpan.FromMilliseconds(seekTarget) : TimeSpan.Zero;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed && Volatile.Read(ref _openGeneration) == generation)
            {
                IsPlaying = false;
                Position = uiPos;
            }
        });
    }

    private void OnVlcPlaying(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _pauseOnFirstPlay) == 1)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed)
                {
                    IsPlaying = false;
                }
            });
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                IsPlaying = true;
            }
        });
    }

    private void OnVlcPaused(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                IsPlaying = false;
            }
        });
    }

    private void OnVlcStopped(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                IsPlaying = false;
            }
        });
    }

    private void OnVlcEndReached(object? sender, EventArgs e)
    {
        bool wasPreRoll = Interlocked.Exchange(ref _pauseOnFirstPlay, 0) == 1;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                if (wasPreRoll)
                {
                    _mediaPlayer.Mute = IsMuted;
                }

                IsPlaying = false;
                Position = Duration;
            }
        });
    }

    private void OnVlcEncounteredError(object? sender, EventArgs e)
    {
        Interlocked.Exchange(ref _pauseOnFirstPlay, 0);
        _logger.Error("LibVLC a rencontre une erreur de lecture.");
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                _mediaPlayer.Mute = IsMuted;
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
