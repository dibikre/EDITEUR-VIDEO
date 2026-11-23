namespace VideoSplitter.Services;

public interface IAppLogger
{
    string LogDirectory { get; }

    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}
