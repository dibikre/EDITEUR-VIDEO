using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using Microsoft.Extensions.DependencyInjection;
using VideoSplitter.Services;
using VideoSplitter.ViewModels;
using VideoSplitter.Views;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace VideoSplitter;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private IAppLogger? _logger;
    private IClassicDesktopStyleApplicationLifetime? _desktopLifetime;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        RegisterGlobalExceptionHandlers();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktopLifetime = desktop;
            desktop.Exit += OnDesktopExit;

            try
            {
                var services = new ServiceCollection();
                ConfigureServices(services);
                _serviceProvider = services.BuildServiceProvider();

                _logger = _serviceProvider.GetRequiredService<IAppLogger>();
                _logger.Info("Demarrage de VideoSplitter (Avalonia)...");

                Core.Initialize();
                _logger.Info("LibVLCSharp Core initialise.");

                // Resoudre LibVLC et MediaPlayer en amont pour valider l'initialisation native.
                _ = _serviceProvider.GetRequiredService<VlcMediaPlayer>();
                _logger.Info("LibVLC et MediaPlayer prets.");

                MainWindow mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
                desktop.MainWindow = mainWindow;

                ICacheService cacheService = _serviceProvider.GetRequiredService<ICacheService>();
                _ = cacheService.CleanupAsync(TimeSpan.FromDays(30), CancellationToken.None);

                ExportViewModel exportViewModel = _serviceProvider.GetRequiredService<ExportViewModel>();
                _ = exportViewModel.DetectEncodersInBackgroundAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                _logger?.Error("Erreur critique lors du demarrage de l'application.", ex);
                desktop.MainWindow = CreateStartupErrorDialog(ex.ToString());
                base.OnFrameworkInitializationCompleted();
                return;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IAppLogger, FileLogger>();
        services.AddSingleton(_ => new LibVLC("--avcodec-hw=any", "--no-video-title-show"));
        services.AddSingleton(sp => new VlcMediaPlayer(sp.GetRequiredService<LibVLC>()));
        services.AddSingleton<IFfmpegService, FfmpegService>();
        services.AddSingleton<ICacheService, CacheService>();
        services.AddSingleton<IThumbnailService, ThumbnailService>();
        services.AddSingleton<IExportService, ExportService>();
        services.AddSingleton<IExportQueue, ExportQueue>();

        services.AddSingleton<PlayerViewModel>();
        services.AddSingleton<TimelineViewModel>();
        services.AddSingleton<EditViewModel>();
        services.AddSingleton<ExportViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddTransient<MainWindow>();
    }

    private void RegisterGlobalExceptionHandlers()
    {
        Dispatcher.UIThread.UnhandledException += OnUiThreadUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnUiThreadUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Error("Exception non geree sur le thread UI.", e.Exception);
        ShowErrorOnUiThread(
            "Une erreur inattendue s'est produite dans l'interface.",
            e.Exception.Message);
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        string detail = ex?.Message ?? e.ExceptionObject?.ToString() ?? "Erreur inconnue.";
        _logger?.Error("Exception non geree dans AppDomain.", ex);

        if (e.IsTerminating && _logger is IDisposable disposableLogger)
        {
            disposableLogger.Dispose();
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.Error("Exception non observee dans une tache en arriere-plan.", e.Exception);
        e.SetObserved();
    }

    private MessageDialog CreateStartupErrorDialog(string detail)
    {
        string logHint = _logger is not null
            ? $"\n\nJournal : {_logger.LogDirectory}"
            : string.Empty;

        var dialog = new MessageDialog(
            "VideoSplitter — Erreur de démarrage",
            $"Impossible de démarrer VideoSplitter.\n\nDétail : {detail}{logHint}",
            isConfirmation: false)
        {
            Width = 680,
            ShowInTaskbar = true,
            WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterScreen
        };

        return dialog;
    }

    private void ShowErrorOnUiThread(string summary, string detail)
    {
        string logHint = _logger is not null
            ? $"\n\nJournal : {_logger.LogDirectory}"
            : string.Empty;

        string message = $"{summary}\n\nDetail : {detail}{logHint}";

        if (Dispatcher.UIThread.CheckAccess())
        {
            var dialog = new MessageDialog("VideoSplitter — Erreur", message, isConfirmation: false);
            if (_desktopLifetime?.MainWindow is { IsVisible: true } owner)
            {
                _ = dialog.ShowDialog(owner);
            }
            else
            {
                dialog.Show();
            }
        }
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        Dispatcher.UIThread.UnhandledException -= OnUiThreadUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

        if (_desktopLifetime is not null)
        {
            _desktopLifetime.Exit -= OnDesktopExit;
            _desktopLifetime = null;
        }

        _logger?.Info("Fermeture de VideoSplitter...");

        // ServiceProvider libere les singletons (MediaPlayer, LibVLC, FileLogger) dans l'ordre inverse.
        _serviceProvider?.Dispose();
        _serviceProvider = null;
    }
}
