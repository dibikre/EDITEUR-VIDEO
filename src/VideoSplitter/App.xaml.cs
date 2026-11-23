using System.Windows;
using System.Windows.Threading;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        RegisterGlobalExceptionHandlers();

        try
        {
            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();

            _logger = _serviceProvider.GetRequiredService<IAppLogger>();
            _logger.Info("Demarrage de VideoSplitter...");

            Core.Initialize();
            _logger.Info("LibVLCSharp Core initialise.");

            // Resoudre LibVLC et MediaPlayer en amont pour valider l'initialisation native.
            _ = _serviceProvider.GetRequiredService<VlcMediaPlayer>();
            _logger.Info("LibVLC et MediaPlayer prets.");

            MainWindow mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            MainWindow = mainWindow;
            mainWindow.Show();

            ICacheService cacheService = _serviceProvider.GetRequiredService<ICacheService>();
            _ = cacheService.CleanupAsync(TimeSpan.FromDays(30), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger?.Error("Erreur critique lors du demarrage de l'application.", ex);
            ShowErrorMessage(
                "Impossible de demarrer VideoSplitter.",
                ex.Message);
            Shutdown(1);
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IAppLogger, FileLogger>();
        services.AddSingleton(_ => new LibVLC("--avcodec-hw=any", "--no-video-title-show"));
        services.AddSingleton(sp => new VlcMediaPlayer(sp.GetRequiredService<LibVLC>()));
        services.AddSingleton<IFfmpegService, FfmpegService>();
        services.AddSingleton<ICacheService, CacheService>();
        services.AddSingleton<IThumbnailService, ThumbnailService>();

        services.AddSingleton<PlayerViewModel>();
        services.AddSingleton<TimelineViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddTransient<MainWindow>();
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Error("Exception non geree sur le thread UI.", e.Exception);
        ShowErrorMessage(
            "Une erreur inattendue s'est produite dans l'interface.",
            e.Exception.Message);
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        string detail = ex?.Message ?? e.ExceptionObject?.ToString() ?? "Erreur inconnue.";
        _logger?.Error("Exception non geree dans AppDomain.", ex);

        if (e.IsTerminating && _logger is IDisposable disposableLogger)
        {
            disposableLogger.Dispose();
        }

        ShowErrorMessage(
            "Une erreur critique est survenue.",
            detail);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.Error("Exception non observee dans une tache en arriere-plan.", e.Exception);
        e.SetObserved();
    }

    private void ShowErrorMessage(string summary, string detail)
    {
        string logHint = _logger is not null
            ? $"\n\nJournal : {_logger.LogDirectory}"
            : string.Empty;

        MessageBox.Show(
            $"{summary}\n\nDetail : {detail}{logHint}",
            "VideoSplitter — Erreur",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

        _logger?.Info("Fermeture de VideoSplitter...");

        // ServiceProvider libere les singletons (MediaPlayer, LibVLC, FileLogger) dans l'ordre inverse.
        _serviceProvider?.Dispose();
        _serviceProvider = null;

        base.OnExit(e);
    }
}
