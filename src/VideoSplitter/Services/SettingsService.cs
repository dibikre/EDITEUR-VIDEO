using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VideoSplitter.Models;

namespace VideoSplitter.Services;

public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IAppLogger _logger;
    private readonly string _settingsPath;

    public SettingsService(IAppLogger logger)
    {
        _logger = logger;
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _settingsPath = Path.Combine(localAppData, "VideoSplitter", "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            string json = File.ReadAllText(_settingsPath);
            AppSettings? parsed = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            return Normalize(parsed);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Impossible de lire les parametres ({_settingsPath}), valeurs par defaut utilisees : {ex.Message}");
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            AppSettings normalized = Normalize(settings);
            string json = JsonSerializer.Serialize(normalized, JsonOptions);
            string tmpPath = _settingsPath + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, _settingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Impossible d'enregistrer les parametres ({_settingsPath}) : {ex.Message}");
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

        if (folderMode == OutputFolderMode.CustomFolder && string.IsNullOrWhiteSpace(customFolder))
        {
            folderMode = OutputFolderMode.SameAsSource;
        }

        return new AppSettings
        {
            ExportMode = mode,
            OutputFolderMode = folderMode,
            CustomOutputFolder = customFolder,
            ShowVolumeGauge = raw.ShowVolumeGauge
        };
    }
}
