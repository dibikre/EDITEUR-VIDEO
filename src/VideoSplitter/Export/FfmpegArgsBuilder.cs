using System.Globalization;
using System.IO;
using VideoSplitter.Models;

namespace VideoSplitter.Export;

public static class FfmpegArgsBuilder
{
    private const int EncoderTestCrf = 23;

    public static List<string> BuildExportArgs(ExportJob job, string temporaryOutputPath)
    {
        var args = new List<string>(32)
        {
            "-hide_banner",
            "-nostdin",
            "-y",
            "-ss", FormatSeconds(job.Segment.Start),
            "-i", job.Media.Path,
            "-t", FormatSeconds(job.Segment.Duration),
            "-map", "0:v:0",
            "-map", "0:a?",
            "-sn",
            "-dn",
            "-map_chapters", "-1"
        };

        string ext = Path.GetExtension(temporaryOutputPath);
        if (job.Mode == ExportMode.Fast)
        {
            args.Add("-c");
            args.Add("copy");
            args.Add("-avoid_negative_ts");
            args.Add("make_zero");
            if (IsFastStartExtension(ext))
            {
                args.Add("-movflags");
                args.Add("+faststart");
            }
        }
        else
        {
            AppendVideoEncoderArgs(args, job.Encoder, job.Crf);
            args.Add("-c:a");
            args.Add("aac");
            args.Add("-b:a");
            args.Add("192k");

            if (IsFastStartExtension(ext))
            {
                args.Add("-movflags");
                args.Add("+faststart");
            }
        }

        args.Add("-progress");
        args.Add("pipe:1");
        args.Add("-nostats");
        args.Add(temporaryOutputPath);

        return args;
    }

    public static List<string> BuildFastVideoChunkArgs(
        string inputPath,
        TimeSpan start,
        TimeSpan duration,
        string outputPath)
    {
        return
        [
            "-hide_banner",
            "-nostdin",
            "-y",
            "-ss", FormatSeconds(start),
            "-i", inputPath,
            "-t", FormatSeconds(duration),
            "-map", "0:v:0",
            "-an",
            "-sn",
            "-dn",
            "-map_chapters", "-1",
            "-c:v", "copy",
            "-avoid_negative_ts", "make_zero",
            outputPath
        ];
    }

    public static List<string> BuildFastAudioChunkArgs(
        string inputPath,
        double startSeconds,
        double durationSeconds,
        string outputPath)
    {
        double clampedStart = Math.Max(0.0, startSeconds);
        double clampedDuration = Math.Max(0.04, durationSeconds);

        var args = new List<string>(26)
        {
            "-hide_banner",
            "-nostdin",
            "-y"
        };

        if (clampedStart > 0.5)
        {
            args.Add("-ss");
            args.Add(FormatSeconds(clampedStart - 0.5));
            args.Add("-i");
            args.Add(inputPath);
            args.Add("-vn");
            args.Add("-ss");
            args.Add("0.5");
        }
        else
        {
            args.Add("-ss");
            args.Add("0");
            args.Add("-i");
            args.Add(inputPath);
            args.Add("-vn");
            if (clampedStart > 0.001)
            {
                args.Add("-ss");
                args.Add(FormatSeconds(clampedStart));
            }
        }

        args.Add("-t");
        args.Add(FormatSeconds(clampedDuration));
        args.Add("-map");
        args.Add("0:a:0");
        args.Add("-sn");
        args.Add("-dn");
        args.Add("-map_chapters");
        args.Add("-1");
        args.Add("-c:a");
        args.Add("copy");
        args.Add("-avoid_negative_ts");
        args.Add("make_zero");
        args.Add(outputPath);

        return args;
    }

    public static List<string> BuildFastConcatArgs(
        string videoListPath,
        string? audioListPath,
        string temporaryOutputPath)
    {
        var args = new List<string>(28)
        {
            "-hide_banner",
            "-nostdin",
            "-y",
            "-f", "concat",
            "-safe", "0",
            "-i", videoListPath
        };

        if (!string.IsNullOrEmpty(audioListPath))
        {
            args.Add("-f");
            args.Add("concat");
            args.Add("-safe");
            args.Add("0");
            args.Add("-i");
            args.Add(audioListPath);
        }

        args.Add("-map");
        args.Add("0:v:0");

        if (!string.IsNullOrEmpty(audioListPath))
        {
            args.Add("-map");
            args.Add("1:a:0");
        }

        args.Add("-sn");
        args.Add("-dn");
        args.Add("-map_chapters");
        args.Add("-1");
        args.Add("-c");
        args.Add("copy");

        string ext = Path.GetExtension(temporaryOutputPath);
        if (IsFastStartExtension(ext))
        {
            args.Add("-movflags");
            args.Add("+faststart");
        }

        args.Add("-progress");
        args.Add("pipe:1");
        args.Add("-nostats");
        args.Add(temporaryOutputPath);

        return args;
    }

    public static string BuildPreciseConcatFilterScript(int segmentCount, bool hasAudio)
    {
        var sb = new System.Text.StringBuilder(segmentCount * 20);
        for (int i = 0; i < segmentCount; i++)
        {
            sb.Append('[').Append(i).Append(":v:0]");
            if (hasAudio)
            {
                sb.Append('[').Append(i).Append(":a:0]");
            }
        }

        sb.Append("concat=n=").Append(segmentCount)
          .Append(":v=1:a=").Append(hasAudio ? "1" : "0")
          .Append(hasAudio ? "[v][a]" : "[v]");
        return sb.ToString();
    }

    public static List<string> BuildPreciseConcatArgs(
        SingleVideoExportJob job,
        string filterScriptPath,
        string temporaryOutputPath)
    {
        var args = new List<string>(job.Segments.Count * 6 + 28)
        {
            "-hide_banner",
            "-nostdin",
            "-y"
        };

        for (int i = 0; i < job.Segments.Count; i++)
        {
            Segment seg = job.Segments[i];
            args.Add("-ss");
            args.Add(FormatSeconds(seg.Start));
            args.Add("-t");
            args.Add(FormatSeconds(seg.Duration));
            args.Add("-i");
            args.Add(job.Media.Path);
        }

        args.Add("-filter_complex_script");
        args.Add(filterScriptPath);
        args.Add("-map");
        args.Add("[v]");

        if (job.Media.HasAudio)
        {
            args.Add("-map");
            args.Add("[a]");
        }

        args.Add("-sn");
        args.Add("-dn");
        args.Add("-map_chapters");
        args.Add("-1");

        AppendVideoEncoderArgs(args, job.Encoder, job.Crf);

        if (job.Media.HasAudio)
        {
            args.Add("-c:a");
            args.Add("aac");
            args.Add("-b:a");
            args.Add("192k");
        }

        string ext = Path.GetExtension(temporaryOutputPath);
        if (IsFastStartExtension(ext))
        {
            args.Add("-movflags");
            args.Add("+faststart");
        }

        args.Add("-progress");
        args.Add("pipe:1");
        args.Add("-nostats");
        args.Add(temporaryOutputPath);

        return args;
    }

    public static List<string> BuildEncoderTestArgs(EncoderKind encoder)
    {
        var args = new List<string>(24)
        {
            "-hide_banner",
            "-nostdin",
            "-f", "lavfi",
            "-i", "testsrc2=size=1280x720:rate=30:duration=1"
        };

        AppendVideoEncoderArgs(args, encoder, EncoderTestCrf);

        args.Add("-f");
        args.Add("null");
        args.Add("-");

        return args;
    }

    private static string FormatSeconds(double seconds)
    {
        double safe = Math.Max(0.0, seconds);
        return safe.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static void AppendVideoEncoderArgs(List<string> args, EncoderKind encoder, int crf)
    {
        string crfText = crf.ToString(CultureInfo.InvariantCulture);

        args.Add("-pix_fmt");
        args.Add("yuv420p");

        switch (encoder)
        {
            case EncoderKind.Cpu:
                args.Add("-c:v");
                args.Add("libx264");
                args.Add("-preset");
                args.Add("veryfast");
                args.Add("-crf");
                args.Add(crfText);
                break;

            case EncoderKind.Nvenc:
                args.Add("-c:v");
                args.Add("h264_nvenc");
                args.Add("-preset");
                args.Add("p4");
                args.Add("-rc");
                args.Add("vbr");
                args.Add("-cq");
                args.Add(crfText);
                args.Add("-b:v");
                args.Add("0");
                break;

            case EncoderKind.Qsv:
                args.Add("-c:v");
                args.Add("h264_qsv");
                args.Add("-preset");
                args.Add("veryfast");
                args.Add("-global_quality");
                args.Add(crfText);
                break;

            case EncoderKind.Amf:
                args.Add("-c:v");
                args.Add("h264_amf");
                args.Add("-quality");
                args.Add("balanced");
                args.Add("-rc");
                args.Add("cqp");
                args.Add("-qp_i");
                args.Add(crfText);
                args.Add("-qp_p");
                args.Add(crfText);
                args.Add("-qp_b");
                args.Add(crfText);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(encoder), encoder, "Encodeur inconnu.");
        }
    }

    private static string FormatSeconds(TimeSpan duration)
    {
        double seconds = Math.Max(0.0, duration.TotalSeconds);
        return seconds.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static bool IsFastStartExtension(string? extension)
    {
        return string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase)
               || string.Equals(extension, ".m4v", StringComparison.OrdinalIgnoreCase)
               || string.Equals(extension, ".mov", StringComparison.OrdinalIgnoreCase);
    }
}
