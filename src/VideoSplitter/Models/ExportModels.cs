namespace VideoSplitter.Models;

public enum ExportMode
{
    Fast,
    Precise
}

public enum ExportOutputType
{
    SeparateSegments,
    SingleVideo
}

public enum EncoderKind
{
    Cpu,
    Nvenc,
    Qsv,
    Amf
}

public enum EncoderSelection
{
    Auto,
    Cpu,
    Nvenc,
    Qsv,
    Amf
}

public enum ConflictPolicy
{
    Overwrite,
    AutoRename,
    Skip
}

public enum ExportOutcome
{
    Success,
    Failed,
    Cancelled,
    Skipped
}

public sealed record EncoderCapabilities(IReadOnlySet<EncoderKind> Available);

public sealed record ExportJob(
    MediaInfo Media,
    Segment Segment,
    int Index,
    int Total,
    string OutputPath,
    ExportMode Mode,
    EncoderKind Encoder,
    int Crf,
    bool AllowCpuFallback = false,
    bool IsSkipped = false);

public sealed record SingleVideoExportJob(
    Guid JobId,
    MediaInfo Media,
    IReadOnlyList<Segment> Segments,
    IReadOnlyList<double> Keyframes,
    string OutputPath,
    ExportMode Mode,
    EncoderKind Encoder,
    int Crf,
    bool AllowCpuFallback = false,
    bool IsSkipped = false);

public sealed record ExportProgress(
    Guid SegmentId,
    double Fraction,
    TimeSpan Elapsed);

public sealed record ExportResult(
    ExportOutcome Outcome,
    string? ErrorMessage,
    string? TechnicalDetails,
    string? FinalPath,
    EncoderKind? UsedEncoder,
    TimeSpan Elapsed);
