using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace VideoSplitter.Services;

public sealed class FileLogger : IAppLogger, IDisposable
{
    private readonly BlockingCollection<LogEntry> _queue = new();
    private readonly Thread _writerThread;
    private readonly Lock _disposeLock = new();
    private bool _disposed;

    public string LogDirectory { get; }

    public FileLogger()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        LogDirectory = Path.Combine(localAppData, "VideoSplitter", "logs");
        Directory.CreateDirectory(LogDirectory);

        _writerThread = new Thread(ProcessQueue)
        {
            IsBackground = true,
            Name = "VideoSplitter.FileLogger"
        };
        _writerThread.Start();
    }

    public void Info(string message) => Enqueue("INFO", message, null);

    public void Warn(string message) => Enqueue("WARN", message, null);

    public void Error(string message, Exception? exception = null) => Enqueue("ERROR", message, exception);

    private void Enqueue(string level, string message, Exception? exception)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _queue.Add(new LogEntry(DateTime.Now, level, Environment.CurrentManagedThreadId, message, exception));
        }
        catch (InvalidOperationException)
        {
            // Ignore writes attempted after CompleteAdding during shutdown.
        }
    }

    private void ProcessQueue()
    {
        DateOnly? currentDate = null;
        StreamWriter? writer = null;

        try
        {
            foreach (LogEntry entry in _queue.GetConsumingEnumerable())
            {
                try
                {
                    DateOnly entryDate = DateOnly.FromDateTime(entry.Timestamp);
                    if (writer is null || currentDate != entryDate)
                    {
                        writer?.Dispose();
                        writer = OpenWriterForDate(entryDate);
                        currentDate = entryDate;
                    }

                    writer.WriteLine(FormatEntry(entry));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[FileLogger] Echec d'ecriture dans le journal : {ex.Message}");
                }
            }
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private StreamWriter OpenWriterForDate(DateOnly date)
    {
        string fileName = $"app-{date:yyyyMMdd}.log";
        string fullPath = Path.Combine(LogDirectory, fileName);
        var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
    }

    private static string FormatEntry(LogEntry entry)
    {
        var sb = new StringBuilder(128);
        sb.Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        sb.Append(" [").Append(entry.Level).Append("] ");
        sb.Append("[T").Append(entry.ThreadId).Append("] ");
        sb.Append(entry.Message);

        if (entry.Exception is not null)
        {
            sb.AppendLine();
            sb.Append(entry.Exception.ToString());
        }

        return sb.ToString();
    }

    public void Dispose()
    {
        lock (_disposeLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _queue.CompleteAdding();
        }

        if (Thread.CurrentThread == _writerThread || _writerThread.Join(TimeSpan.FromSeconds(2)))
        {
            _queue.Dispose();
        }
    }

    private sealed record LogEntry(
        DateTime Timestamp,
        string Level,
        int ThreadId,
        string Message,
        Exception? Exception);
}
