namespace AgentContinuum.Core.Platform;

/// <summary>Appends one UTC-stamped line per message to a file.</summary>
public sealed class FileLog : ILog
{
    private readonly string _path;
    private readonly IClock _clock;
    private readonly object _gate = new();

    public FileLog(string path, IClock clock)
    {
        _path = path;
        _clock = clock;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    // The launch threads and the daemon loop log at once, and interleaved appends could tear a line.
    public void Write(string message)
    {
        var line = $"{_clock.UtcNow.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ} {message}{Environment.NewLine}";
        lock (_gate)
        {
            File.AppendAllText(_path, line);
        }
    }
}
