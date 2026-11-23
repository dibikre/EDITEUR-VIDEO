using System.Globalization;
using System.IO;
using VideoSplitter.Models;

namespace VideoSplitter.Export;

public static class ExportPlanner
{
    public const string DefaultPattern = "{nom}_part{NN}";
    private const int MaxFullPathLength = 239;

    private static readonly HashSet<string> PreciseCompatibleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".m4v", ".ts"
    };

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string ResolveOutputExtension(string sourceExtension, ExportMode mode)
    {
        string normalized = string.IsNullOrWhiteSpace(sourceExtension)
            ? ".mp4"
            : (sourceExtension.StartsWith('.') ? sourceExtension : "." + sourceExtension);

        if (mode == ExportMode.Fast)
        {
            return normalized;
        }

        return PreciseCompatibleExtensions.Contains(normalized) ? normalized : ".mp4";
    }

    public static string BuildFileName(
        string pattern,
        string sourceName,
        int index,
        int total,
        Segment segment)
    {
        string effectivePattern = string.IsNullOrWhiteSpace(pattern) ? DefaultPattern : pattern.Trim();
        if (!effectivePattern.Contains("{NN}", StringComparison.Ordinal))
        {
            effectivePattern += "_{NN}";
        }

        int digits = Math.Max(2, total.ToString(CultureInfo.InvariantCulture).Length);
        string nn = index.ToString($"D{digits}", CultureInfo.InvariantCulture);
        string startFormatted = FormatStartTime(segment.Start);

        string safeSource = SanitizeComponent(sourceName, fallback: "video");
        string safeSegment = SanitizeComponent(segment.Name, fallback: $"Segment_{nn}");

        string rawName = effectivePattern
            .Replace("{nom}", safeSource, StringComparison.Ordinal)
            .Replace("{NN}", nn, StringComparison.Ordinal)
            .Replace("{debut}", startFormatted, StringComparison.Ordinal)
            .Replace("{segment}", safeSegment, StringComparison.Ordinal);

        return SanitizeComponent(rawName, fallback: $"video_part{nn}");
    }

    public static string BuildSingleVideoFileName(
        string pattern,
        string sourceName,
        IReadOnlyList<Segment> segments)
    {
        string safeSource = SanitizeComponent(sourceName, fallback: "video");
        string effectivePattern = string.IsNullOrWhiteSpace(pattern) ? DefaultPattern : pattern.Trim();

        if (string.Equals(effectivePattern, DefaultPattern, StringComparison.Ordinal))
        {
            return SanitizeComponent($"{safeSource}_complet", fallback: "video_complet");
        }

        Segment firstSegment = segments.Count > 0
            ? segments[0]
            : new Segment(Guid.Empty, TimeSpan.Zero, TimeSpan.Zero, true, "Complet");

        string startFormatted = FormatStartTime(firstSegment.Start);
        string safeSegment = SanitizeComponent(firstSegment.Name, fallback: "Complet");

        string rawName = effectivePattern
            .Replace("_part{NN}", string.Empty, StringComparison.Ordinal)
            .Replace("-part{NN}", string.Empty, StringComparison.Ordinal)
            .Replace("_{NN}", string.Empty, StringComparison.Ordinal)
            .Replace("-{NN}", string.Empty, StringComparison.Ordinal)
            .Replace("{NN}", string.Empty, StringComparison.Ordinal)
            .Replace("{nom}", safeSource, StringComparison.Ordinal)
            .Replace("{debut}", startFormatted, StringComparison.Ordinal)
            .Replace("{segment}", safeSegment, StringComparison.Ordinal)
            .Trim(' ', '_', '-');

        if (string.IsNullOrWhiteSpace(rawName)
            || string.Equals(rawName, safeSource, StringComparison.OrdinalIgnoreCase))
        {
            rawName = $"{safeSource}_complet";
        }

        return SanitizeComponent(rawName, fallback: $"{safeSource}_complet");
    }

    public static string BuildSingleVideoOutputPath(
        string outputFolder,
        string pattern,
        string sourceName,
        string sourceExtension,
        IReadOnlyList<Segment> segments,
        ExportMode mode)
    {
        string ext = ResolveOutputExtension(sourceExtension, mode);
        string baseStem = BuildSingleVideoFileName(pattern, sourceName, segments);
        string truncatedStem = TruncateStemForPathLimit(outputFolder, baseStem, ext, 1, 1);
        return Path.Combine(outputFolder, truncatedStem + ext);
    }

    public static (string OutputPath, bool IsSkipped) ResolveSingleVideoConflict(
        string outputPath,
        ConflictPolicy policy)
    {
        if (!File.Exists(outputPath))
        {
            return (outputPath, false);
        }

        return policy switch
        {
            ConflictPolicy.Overwrite => (outputPath, false),
            ConflictPolicy.Skip => (outputPath, true),
            ConflictPolicy.AutoRename => (FindUniquePath(outputPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase)), false),
            _ => (outputPath, false)
        };
    }

    public static string BuildFullOutputPath(
        string outputFolder,
        string pattern,
        string sourceName,
        string sourceExtension,
        int index,
        int total,
        Segment segment,
        ExportMode mode)
    {
        string ext = ResolveOutputExtension(sourceExtension, mode);
        string baseStem = BuildFileName(pattern, sourceName, index, total, segment);
        string truncatedStem = TruncateStemForPathLimit(outputFolder, baseStem, ext, index, total);
        return Path.Combine(outputFolder, truncatedStem + ext);
    }

    public static List<ExportJob> BuildJobs(
        MediaInfo media,
        IReadOnlyList<Segment> segments,
        string outputFolder,
        string pattern,
        ExportMode mode,
        EncoderKind encoder,
        int crf,
        bool allowCpuFallback = false)
    {
        var enabledSegments = new List<Segment>(segments.Count);
        for (int i = 0; i < segments.Count; i++)
        {
            if (segments[i].Enabled)
            {
                enabledSegments.Add(segments[i]);
            }
        }

        int total = enabledSegments.Count;
        var jobs = new List<ExportJob>(total);
        if (total == 0)
        {
            return jobs;
        }

        string sourceName = Path.GetFileNameWithoutExtension(media.Path);
        string sourceExt = Path.GetExtension(media.Path);

        for (int i = 0; i < total; i++)
        {
            Segment segment = enabledSegments[i];
            int oneBasedIndex = i + 1;
            string outputPath = BuildFullOutputPath(
                outputFolder,
                pattern,
                sourceName,
                sourceExt,
                oneBasedIndex,
                total,
                segment,
                mode);

            jobs.Add(new ExportJob(
                Media: media,
                Segment: segment,
                Index: oneBasedIndex,
                Total: total,
                OutputPath: outputPath,
                Mode: mode,
                Encoder: encoder,
                Crf: crf,
                AllowCpuFallback: allowCpuFallback));
        }

        return jobs;
    }

    public static List<ExportJob> ResolveConflicts(IReadOnlyList<ExportJob> jobs, ConflictPolicy policy)
    {
        var resolved = new List<ExportJob>(jobs.Count);
        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < jobs.Count; i++)
        {
            ExportJob job = jobs[i];
            bool exists = File.Exists(job.OutputPath) || reservedPaths.Contains(job.OutputPath);

            if (!exists)
            {
                reservedPaths.Add(job.OutputPath);
                resolved.Add(job);
                continue;
            }

            switch (policy)
            {
                case ConflictPolicy.Overwrite:
                    reservedPaths.Add(job.OutputPath);
                    resolved.Add(job);
                    break;

                case ConflictPolicy.Skip:
                    resolved.Add(job with { IsSkipped = true });
                    break;

                case ConflictPolicy.AutoRename:
                    string uniquePath = FindUniquePath(job.OutputPath, reservedPaths);
                    reservedPaths.Add(uniquePath);
                    resolved.Add(job with { OutputPath = uniquePath });
                    break;

                default:
                    resolved.Add(job);
                    break;
            }
        }

        return resolved;
    }

    public static string GetTemporaryPartPath(string finalOutputPath)
    {
        string dir = Path.GetDirectoryName(finalOutputPath) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(finalOutputPath);
        string ext = Path.GetExtension(finalOutputPath);
        return Path.Combine(dir, $"{stem}.part{ext}");
    }

    public static (bool Ok, string? ErrorMessage) ValidateOutputDirectory(string outputFolder)
    {
        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            return (false, "Veuillez sélectionner un dossier de sortie valide.");
        }

        try
        {
            Directory.CreateDirectory(outputFolder);
            string probePath = Path.Combine(outputFolder, $".videosplitter_write_test_{Guid.NewGuid():N}.tmp");
            using (File.Create(probePath))
            {
            }

            File.Delete(probePath);
            return (true, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (false, $"Accès refusé au dossier de sortie :\n{outputFolder}");
        }
        catch (Exception ex)
        {
            return (false, $"Impossible d'écrire dans le dossier de sortie :\n{outputFolder}\n\nDétail : {ex.Message}");
        }
    }

    public static long EstimateRequiredBytes(MediaInfo media, IReadOnlyList<ExportJob> jobs)
    {
        double includedSeconds = 0.0;
        for (int i = 0; i < jobs.Count; i++)
        {
            if (!jobs[i].IsSkipped)
            {
                includedSeconds += Math.Max(0.0, jobs[i].Segment.Duration.TotalSeconds);
            }
        }

        return EstimateRequiredBytesFromSeconds(media, includedSeconds);
    }

    public static long EstimateRequiredBytes(MediaInfo media, IReadOnlyList<Segment> segments)
    {
        double includedSeconds = 0.0;
        for (int i = 0; i < segments.Count; i++)
        {
            if (segments[i].Enabled)
            {
                includedSeconds += Math.Max(0.0, segments[i].Duration.TotalSeconds);
            }
        }

        return EstimateRequiredBytesFromSeconds(media, includedSeconds);
    }

    private static long EstimateRequiredBytesFromSeconds(MediaInfo media, double includedSeconds)
    {
        if (includedSeconds <= 0.0 || media.Duration <= TimeSpan.Zero)
        {
            return 0L;
        }

        long sourceBytes;
        try
        {
            var info = new FileInfo(media.Path);
            sourceBytes = info.Exists ? info.Length : 0L;
        }
        catch
        {
            return 0L;
        }

        if (sourceBytes <= 0)
        {
            return 0L;
        }

        double ratio = includedSeconds / media.Duration.TotalSeconds;
        return (long)Math.Ceiling(sourceBytes * ratio * 1.1);
    }

    public static long? GetAvailableFreeSpaceBytes(string outputFolder)
    {
        try
        {
            string fullPath = Path.GetFullPath(outputFolder);
            string? root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return null;
            }

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return null;
        }
    }

    private static string FindUniquePath(string originalPath, HashSet<string> reservedPaths)
    {
        string dir = Path.GetDirectoryName(originalPath) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(originalPath);
        string ext = Path.GetExtension(originalPath);

        int suffixNum = 1;
        while (true)
        {
            string suffix = $" ({suffixNum})";
            string candidateStem = FitStemWithSuffix(dir, stem, suffix, ext);
            string candidate = Path.Combine(dir, candidateStem + ext);
            if (!File.Exists(candidate) && !reservedPaths.Contains(candidate))
            {
                return candidate;
            }

            suffixNum++;
        }
    }

    private static string TruncateStemForPathLimit(
        string outputFolder,
        string stem,
        string extension,
        int index,
        int total)
    {
        // Rester sous 240 caracteres en incluant le suffixe temporaire ".part"
        int overhead = outputFolder.Length + 1 + ".part".Length + extension.Length;
        int maxStemLength = MaxFullPathLength - overhead;
        if (maxStemLength < 8)
        {
            maxStemLength = 8;
        }

        if (stem.Length <= maxStemLength)
        {
            return stem;
        }

        int digits = Math.Max(2, total.ToString(CultureInfo.InvariantCulture).Length);
        string uniqueSuffix = $"_{index.ToString($"D{digits}", CultureInfo.InvariantCulture)}";
        int prefixBudget = Math.Max(1, maxStemLength - uniqueSuffix.Length);
        string trimmed = stem[..Math.Min(stem.Length, prefixBudget)].TrimEnd(' ', '.');
        if (string.IsNullOrEmpty(trimmed))
        {
            trimmed = "video";
        }

        return EnsureNotReserved(trimmed + uniqueSuffix);
    }

    private static string FitStemWithSuffix(string dir, string stem, string suffix, string extension)
    {
        int overhead = dir.Length + 1 + ".part".Length + extension.Length + suffix.Length;
        int maxPrefix = Math.Max(1, MaxFullPathLength - overhead);
        string prefix = stem.Length <= maxPrefix ? stem : stem[..maxPrefix].TrimEnd(' ', '.');
        if (string.IsNullOrEmpty(prefix))
        {
            prefix = "video";
        }

        return EnsureNotReserved(prefix + suffix);
    }

    private static string SanitizeComponent(string? input, string fallback)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return fallback;
        }

        char[] invalidChars = Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            buffer[i] = Array.IndexOf(invalidChars, c) >= 0 ? '_' : c;
        }

        string sanitized = new string(buffer).TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return fallback;
        }

        return EnsureNotReserved(sanitized);
    }

    private static string EnsureNotReserved(string name)
    {
        int dotIndex = name.IndexOf('.');
        string rootName = dotIndex >= 0 ? name[..dotIndex] : name;
        return ReservedWindowsNames.Contains(rootName) ? "_" + name : name;
    }

    private static string FormatStartTime(TimeSpan start)
    {
        if (start < TimeSpan.Zero)
        {
            start = TimeSpan.Zero;
        }

        int totalHours = (int)start.TotalHours;
        return $"{totalHours:D2}-{start.Minutes:D2}-{start.Seconds:D2}";
    }
}
