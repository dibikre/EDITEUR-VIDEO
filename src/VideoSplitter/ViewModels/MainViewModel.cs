using CommunityToolkit.Mvvm.ComponentModel;
using VideoSplitter.Services;

namespace VideoSplitter.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IAppLogger _logger;

    [ObservableProperty]
    private string _title = "VideoSplitter";

    public MainViewModel(IAppLogger logger)
    {
        _logger = logger;
        _logger.Info("MainViewModel initialise.");
    }
}
