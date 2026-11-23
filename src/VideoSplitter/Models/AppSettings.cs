namespace VideoSplitter.Models;

public enum OutputFolderMode
{
    SameAsSource,
    CustomFolder
}

public sealed class AppSettings
{
    public ExportMode ExportMode { get; set; } = ExportMode.Fast;
    public OutputFolderMode OutputFolderMode { get; set; } = OutputFolderMode.SameAsSource;
    public string CustomOutputFolder { get; set; } = string.Empty;
    public bool ShowVolumeGauge { get; set; } = true;
}
