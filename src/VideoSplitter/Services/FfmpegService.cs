using System.Globalization;
using System.IO;
using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using VideoSplitter.Models;

namespace VideoSplitter.Services;

public sealed class FfmpegService : IFfmpegService
{
    private readonly IAppLogger _logger;
    private readonly string _ffprobePath;

    public FfmpegService(IAppLogger logger)
    {
        _logger = logger;
        _ffprobePath = Path.Combine(AppContext.BaseDirectory, "Assets", "ffmpeg", "ffprobe.exe");
    }

    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(_ffprobePath))
        {
            string details = $"Chemin attendu : {_ffprobePath}";
            _logger.Error($"ffprobe.exe introuvable. {details}");
            throw new MediaProbeException(
                "L'outil ffprobe.exe est introuvable dans le dossier Assets\\ffmpeg. Veuillez exécuter scripts\\fetch-ffmpeg.ps1 puis recompiler.",
                details);
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            string details = $"Fichier demandé : '{path}'";
            _logger.Warn($"Tentative d'ouverture d'un fichier introuvable. {details}");
            throw new MediaProbeException(
                "Le fichier vidéo sélectionné est introuvable ou inaccessible.",
                details);
        }

        _logger.Info($"Analyse ffprobe : {path}");

        BufferedCommandResult result;
        try
        {
            result = await Cli.Wrap(_ffprobePath)
                .WithArguments(["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.Info($"Analyse ffprobe annulée : {path}");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"Échec d'exécution de ffprobe pour {path}", ex);
            throw new MediaProbeException(
                "Impossible de lancer l'analyse du fichier vidéo avec ffprobe.",
                ex.Message,
                ex);
        }

        if (result.ExitCode != 0)
        {
            string stderr = result.StandardError.Trim();
            _logger.Error($"ffprobe a retourné le code {result.ExitCode} pour {path} : {stderr}");
            throw new MediaProbeException(
                "Le fichier vidéo est invalide, corrompu ou dans un format non pris en charge.",
                $"Code de sortie {result.ExitCode} : {stderr}");
        }

        try
        {
            MediaInfo mediaInfo = ParseProbeJson(path, result.StandardOutput);
            _logger.Info(
                $"Analyse terminée : {Path.GetFileName(path)} | {mediaInfo.Width}x{mediaInfo.Height} | {mediaInfo.Fps:F3} fps | {mediaInfo.VideoCodec} | Audio: {(mediaInfo.HasAudio ? mediaInfo.AudioCodec : "aucun")} | Durée: {mediaInfo.Duration:hh\\:mm\\:ss\\.fff}");
            return mediaInfo;
        }
        catch (MediaProbeException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"Erreur de lecture de la sortie JSON ffprobe pour {path}", ex);
            throw new MediaProbeException(
                "Les métadonnées du fichier vidéo n'ont pas pu être interprétées.",
                ex.Message,
                ex);
        }
    }

    private static MediaInfo ParseProbeJson(string path, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new MediaProbeException("ffprobe n'a retourné aucune métadonnée pour ce fichier.");
        }

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("streams", out JsonElement streamsElement) || streamsElement.ValueKind != JsonValueKind.Array)
        {
            throw new MediaProbeException("Aucun flux média détecté dans ce fichier.");
        }

        JsonElement? videoStream = null;
        JsonElement? audioStream = null;

        foreach (JsonElement stream in streamsElement.EnumerateArray())
        {
            string? codecType = GetString(stream, "codec_type");
            if (codecType == "video" && videoStream is null)
            {
                videoStream = stream;
            }
            else if (codecType == "audio" && audioStream is null)
            {
                audioStream = stream;
            }
        }

        if (videoStream is null)
        {
            throw new MediaProbeException("Ce fichier ne contient aucun flux vidéo exploitable.");
        }

        JsonElement vs = videoStream.Value;
        int width = GetInt(vs, "width");
        int height = GetInt(vs, "height");
        string videoCodec = GetString(vs, "codec_name") ?? "inconnu";

        double fps = ParseFraction(GetString(vs, "avg_frame_rate"));
        if (fps <= 0)
        {
            fps = ParseFraction(GetString(vs, "r_frame_rate"));
        }

        if (fps <= 0)
        {
            fps = 25.0;
        }

        bool hasAudio = audioStream is not null;
        string? audioCodec = audioStream is not null ? GetString(audioStream.Value, "codec_name") : null;

        double durationSeconds = 0;
        if (root.TryGetProperty("format", out JsonElement formatElement))
        {
            durationSeconds = ParseDouble(GetString(formatElement, "duration"));
        }

        if (durationSeconds <= 0)
        {
            durationSeconds = ParseDouble(GetString(vs, "duration"));
        }

        if (durationSeconds <= 0)
        {
            throw new MediaProbeException("Impossible de déterminer la durée de cette vidéo.");
        }

        return new MediaInfo(
            Path: path,
            Duration: TimeSpan.FromSeconds(durationSeconds),
            Width: width,
            Height: height,
            Fps: fps,
            VideoCodec: videoCodec,
            AudioCodec: audioCodec,
            HasAudio: hasAudio);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static int GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement prop))
        {
            return 0;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetInt32(out int val) => val,
            JsonValueKind.String when int.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
            _ => 0
        };
    }

    private static double ParseDouble(string? text)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value > 0
            ? value
            : 0;
    }

    private static double ParseFraction(string? fraction)
    {
        if (string.IsNullOrWhiteSpace(fraction))
        {
            return 0;
        }

        int slashIndex = fraction.IndexOf('/');
        if (slashIndex > 0)
        {
            ReadOnlySpan<char> numSpan = fraction.AsSpan(0, slashIndex);
            ReadOnlySpan<char> denSpan = fraction.AsSpan(slashIndex + 1);
            if (double.TryParse(numSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out double num)
                && double.TryParse(denSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out double den)
                && den > 0)
            {
                return num / den;
            }

            return 0;
        }

        return ParseDouble(fraction);
    }
}
