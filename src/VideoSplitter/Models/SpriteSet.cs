using System.Text.Json.Serialization;

namespace VideoSplitter.Models;

public sealed record SpriteSet(
    string Directory,
    double IntervalSeconds,
    int ThumbWidth,
    int ThumbHeight,
    int Columns,
    int Rows,
    int Count,
    int SheetCount);

public sealed class SpriteMetaJson
{
    [JsonPropertyName("interval")]
    public double Interval { get; set; }

    [JsonPropertyName("thumbWidth")]
    public int ThumbWidth { get; set; }

    [JsonPropertyName("thumbHeight")]
    public int ThumbHeight { get; set; }

    [JsonPropertyName("cols")]
    public int Cols { get; set; }

    [JsonPropertyName("rows")]
    public int Rows { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("sheetCount")]
    public int SheetCount { get; set; }

    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    [JsonPropertyName("complete")]
    public bool Complete { get; set; }
}
