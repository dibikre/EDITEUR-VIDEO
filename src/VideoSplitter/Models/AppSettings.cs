using VideoSplitter.Export;

namespace VideoSplitter.Models;

public enum OutputFolderMode
{
    SameAsSource,
    CustomFolder,
    OverwriteSource
}

public sealed class AppSettings
{
    public ExportMode ExportMode { get; set; } = ExportMode.Fast;
    public OutputFolderMode OutputFolderMode { get; set; } = OutputFolderMode.SameAsSource;
    public string CustomOutputFolder { get; set; } = string.Empty;
    public string FileNamePattern { get; set; } = ExportPlanner.DefaultPattern;
    public bool ShowVolumeGauge { get; set; } = true;
    public int Crf { get; set; } = 18;
    public EncoderSelection SelectedEncoder { get; set; } = EncoderSelection.Auto;
    public bool SnapToKeyframes { get; set; } = true;
    public bool FollowPlayback { get; set; } = true;
    public int Volume { get; set; } = 100;
    public bool IsMuted { get; set; }
    public double PlaybackSpeed { get; set; } = 1.0;
}
