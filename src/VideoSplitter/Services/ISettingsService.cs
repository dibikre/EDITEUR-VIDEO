using VideoSplitter.Models;

namespace VideoSplitter.Services;

public interface ISettingsService
{
    AppSettings Load();
    void Save(AppSettings settings);
}
