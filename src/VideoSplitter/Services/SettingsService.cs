using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VideoSplitter.Export;
using VideoSplitter.Models;

namespace VideoSplitter.Services;

public sealed class SettingsService : ISettingsService
{
    private static readonly double[] ValidSpeeds = [0.5, 1.0, 1.5, 2.0];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _syncLock = new();
    private readonly IAppLogger _logger;
    private readonly string _settingsPath;

    public SettingsService(IAppLogger logger)
    {
        _logger = logger;
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = AppContext.BaseDirectory;
        }

        _settingsPath = Path.Combine(localAppData, "VideoSplitter", "settings.json");
    }

    public AppSettings Load()
    {
        lock (_syncLock)
        {
            if (!File.Exists(_settingsPath))
            {
                return new AppSettings();
            }

            try
            {
                string json = File.ReadAllText(_settingsPath);
                AppSettings? parsed = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                AppSettings normalized = Normalize(parsed);
                _logger.Info($"Parametres charges depuis : {_settingsPath}");
                return normalized;
            }
            catch (Exception ex)
            {
                _logger.Warn($"Impossible de lire les parametres ({_settingsPath}), valeurs par defaut utilisees : {ex.Message}");
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_syncLock)
        {
            string tmpPath = _settingsPath + ".tmp";
            try
            {
                string? directory = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                AppSettings normalized = Normalize(settings);
                string json = JsonSerializer.Serialize(normalized, JsonOptions);

                try
                {
                    File.WriteAllText(tmpPath, json);
                    File.Move(tmpPath, _settingsPath, overwrite: true);
                }
                catch (IOException)
                {
                    // Repli direct si un processus Windows (antivirus/indexeur) verrouille brievement le remplacement atomique.
                    File.WriteAllText(_settingsPath, json);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"Impossible d'enregistrer les parametres ({_settingsPath}) : {ex.Message}");
            }
            finally
            {
                try
                {
                    if (File.Exists(tmpPath))
                    {
                        File.Delete(tmpPath);
                    }
                }
                catch
                {
                    // Ignorer si le fichier temporaire a deja ete deplace ou supprime.
                }
            }
        }
    }

    private static AppSettings Normalize(AppSettings? raw)
    {
        if (raw is null)
        {
            return new AppSettings();
        }

        ExportMode mode = Enum.IsDefined(raw.ExportMode) ? raw.ExportMode : ExportMode.Fast;
        OutputFolderMode folderMode = Enum.IsDefined(raw.OutputFolderMode)
            ? raw.OutputFolderMode
            : OutputFolderMode.SameAsSource;
        string customFolder = raw.CustomOutputFolder?.Trim() ?? string.Empty;
        string fileNamePattern = string.IsNullOrWhiteSpace(raw.FileNamePattern)
            ? ExportPlanner.DefaultPattern
            : raw.FileNamePattern.Trim();

        if (folderMode == OutputFolderMode.CustomFolder && string.IsNullOrWhiteSpace(customFolder))
        {
            folderMode = OutputFolderMode.SameAsSource;
        }

        int crf = Math.Clamp(raw.Crf, 14, 28);
        EncoderSelection encoder = Enum.IsDefined(raw.SelectedEncoder)
            ? raw.SelectedEncoder
            : EncoderSelection.Auto;
        int volume = Math.Clamp(raw.Volume, 0, 100);
        double speed = ResolveNearestValidSpeed(raw.PlaybackSpeed);

        return new AppSettings
        {
            ExportMode = mode,
            OutputFolderMode = folderMode,
            CustomOutputFolder = customFolder,
            FileNamePattern = fileNamePattern,
            ShowVolumeGauge = raw.ShowVolumeGauge,
            Crf = crf,
            SelectedEncoder = encoder,
            SnapToKeyframes = raw.SnapToKeyframes,
            FollowPlayback = raw.FollowPlayback,
            Volume = volume,
            IsMuted = raw.IsMuted,
            PlaybackSpeed = speed
        };
    }

    private static double ResolveNearestValidSpeed(double rawSpeed)
    {
        if (double.IsNaN(rawSpeed) || double.IsInfinity(rawSpeed) || rawSpeed <= 0)
        {
            return 1.0;
        }

        foreach (double valid in ValidSpeeds)
        {
            if (Math.Abs(rawSpeed - valid) < 0.01)
            {
                return valid;
            }
        }

        return 1.0;
    }
}
