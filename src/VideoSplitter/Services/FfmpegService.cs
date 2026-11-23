using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using VideoSplitter.Models;

namespace VideoSplitter.Services;

public sealed class FfmpegService : IFfmpegService
{
    private const int MaxStderrLines = 20;
    private const string KeyframesFileName = "keyframes.txt";

    private readonly ICacheService _cacheService;
    private readonly IAppLogger _logger;
    private readonly string _ffprobePath;

    public FfmpegService(ICacheService cacheService, IAppLogger logger)
    {
        _cacheService = cacheService;
        _logger = logger;
        _ffprobePath = Path.Combine(AppContext.BaseDirectory, "Assets", "ffmpeg", "ffprobe.exe");
    }

    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct)
    {
        EnsureFfprobeAndInput(path);

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

    public async Task<IReadOnlyList<double>> GetKeyframesAsync(string path, MediaInfo media, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string cacheDir = _cacheService.GetCacheDirectory(path);
        string cacheFile = Path.Combine(cacheDir, KeyframesFileName);

        IReadOnlyList<double>? cached = await TryReadKeyframesCacheAsync(cacheFile, ct).ConfigureAwait(false);
        if (cached is not null)
        {
            _cacheService.TouchCacheDirectory(cacheDir);
            _logger.Info($"Cache hit pour les keyframes ({cached.Count}) : {cacheDir}");
            return cached;
        }

        _logger.Info($"Cache miss pour les keyframes : {cacheDir}");
        EnsureFfprobeAndInput(path);

        var stopwatch = Stopwatch.StartNew();
        double startTimeSeconds = media.StartTime.TotalSeconds;
        var rawKeyframes = new List<double>();
        var stderrLines = new Queue<string>(MaxStderrLines + 1);
        object stderrLock = new();

        void HandleStdoutLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            int commaIndex = line.IndexOf(',');
            if (commaIndex <= 0 || commaIndex >= line.Length - 1)
            {
                return;
            }

            ReadOnlySpan<char> flagsSpan = line.AsSpan(commaIndex + 1).Trim();
            if (flagsSpan.IndexOf('K') < 0)
            {
                return;
            }

            ReadOnlySpan<char> ptsSpan = line.AsSpan(0, commaIndex).Trim();
            if (ptsSpan.Equals("N/A", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (double.TryParse(ptsSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out double pts)
                && double.IsFinite(pts))
            {
                double adjusted = Math.Max(0.0, pts - startTimeSeconds);
                rawKeyframes.Add(adjusted);
            }
        }

        void HandleStderrLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (stderrLock)
            {
                if (stderrLines.Count >= MaxStderrLines)
                {
                    stderrLines.Dequeue();
                }

                stderrLines.Enqueue(line);
            }
        }

        CommandResult result;
        try
        {
            result = await Cli.Wrap(_ffprobePath)
                .WithArguments(["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", path])
                .WithValidation(CommandResultValidation.None)
                .WithStandardOutputPipe(PipeTarget.ToDelegate(HandleStdoutLine))
                .WithStandardErrorPipe(PipeTarget.ToDelegate(HandleStderrLine))
                .ExecuteAsync(ct, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.Info($"Analyse des keyframes annulée : {path}");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"Échec d'exécution de ffprobe (keyframes) pour {path}", ex);
            throw new MediaProbeException(
                "Impossible d'analyser les keyframes du fichier vidéo.",
                ex.Message,
                ex);
        }

        string stderrTail;
        lock (stderrLock)
        {
            stderrTail = string.Join(" | ", stderrLines);
        }

        if (result.ExitCode != 0)
        {
            _logger.Error($"ffprobe keyframes a retourné le code {result.ExitCode} pour {path} : {stderrTail}");
            throw new MediaProbeException(
                "L'analyse des keyframes a échoué.",
                $"Code de sortie {result.ExitCode} : {stderrTail}");
        }

        ct.ThrowIfCancellationRequested();

        List<double> sortedUnique = NormalizeKeyframes(rawKeyframes);
        stopwatch.Stop();
        _logger.Info($"Keyframes analysées en {stopwatch.Elapsed.TotalSeconds:F2} s ({sortedUnique.Count} keyframe(s)) : {path}");

        if (sortedUnique.Count > 0)
        {
            await WriteKeyframesCacheAtomicAsync(cacheDir, cacheFile, sortedUnique, ct).ConfigureAwait(false);
        }
        else if (!string.IsNullOrEmpty(stderrTail))
        {
            _logger.Warn($"Aucune keyframe détectée pour {path}. Stderr : {stderrTail}");
        }

        return sortedUnique;
    }

    private void EnsureFfprobeAndInput(string path)
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
    }

    private async Task<IReadOnlyList<double>?> TryReadKeyframesCacheAsync(string cacheFile, CancellationToken ct)
    {
        if (!File.Exists(cacheFile))
        {
            return null;
        }

        try
        {
            string[] lines = await File.ReadAllLinesAsync(cacheFile, ct).ConfigureAwait(false);
            var list = new List<double>(lines.Length);
            foreach (string line in lines)
            {
                if (double.TryParse(line.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                    && double.IsFinite(value)
                    && value >= 0.0)
                {
                    list.Add(value);
                }
            }

            if (list.Count == 0)
            {
                return null;
            }

            return NormalizeKeyframes(list);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Cache keyframes illisible ({cacheFile}) : {ex.Message}");
            return null;
        }
    }

    private async Task WriteKeyframesCacheAtomicAsync(
        string cacheDir,
        string cacheFile,
        IReadOnlyList<double> keyframes,
        CancellationToken ct)
    {
        string tmpFile = cacheFile + ".tmp";
        try
        {
            _cacheService.TouchCacheDirectory(cacheDir);
            var sb = new StringBuilder(keyframes.Count * 12);
            for (int i = 0; i < keyframes.Count; i++)
            {
                sb.AppendLine(keyframes[i].ToString("G17", CultureInfo.InvariantCulture));
            }

            await File.WriteAllTextAsync(tmpFile, sb.ToString(), ct).ConfigureAwait(false);
            File.Move(tmpFile, cacheFile, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(tmpFile);
            throw;
        }
        catch (Exception ex)
        {
            TryDeleteFile(tmpFile);
            _logger.Warn($"Impossible d'écrire le cache keyframes ({cacheFile}) : {ex.Message}");
        }
    }

    private static List<double> NormalizeKeyframes(List<double> raw)
    {
        if (raw.Count == 0)
        {
            return raw;
        }

        raw.Sort();
        var dedup = new List<double>(raw.Count);
        double last = double.NaN;
        for (int i = 0; i < raw.Count; i++)
        {
            double val = raw[i];
            if (dedup.Count == 0 || val - last > 1e-6)
            {
                dedup.Add(val);
                last = val;
            }
        }

        return dedup;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignorer l'échec de nettoyage d'un fichier temporaire.
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
        double startTimeSeconds = 0;
        if (root.TryGetProperty("format", out JsonElement formatElement))
        {
            durationSeconds = ParsePositiveDouble(GetString(formatElement, "duration"));
            startTimeSeconds = ParseFiniteDoubleOrDefault(GetString(formatElement, "start_time"));
        }

        if (durationSeconds <= 0)
        {
            durationSeconds = ParsePositiveDouble(GetString(vs, "duration"));
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
            HasAudio: hasAudio,
            StartTime: TimeSpan.FromSeconds(startTimeSeconds));
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

    private static double ParsePositiveDouble(string? text)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            && double.IsFinite(value)
            && value > 0
            ? value
            : 0;
    }

    private static double ParseFiniteDoubleOrDefault(string? text)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            && double.IsFinite(value)
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

        return ParsePositiveDouble(fraction);
    }
}
