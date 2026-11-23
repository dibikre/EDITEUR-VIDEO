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
