using System.Globalization;
using AgentContinuum.Core.Platform;

namespace AgentContinuum.AcTestHost;

/// <summary>The system clock moved by the seconds in a file, read on every call so a test can jump it mid-run.</summary>
public sealed class OffsetClock : IClock
{
    private readonly string? _offsetFile;
    private TimeSpan _last;

    public OffsetClock(string? offsetFile)
    {
        _offsetFile = offsetFile;
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow + Offset();

    // A real clock jump leaves the monotonic count alone, which is how the scheduler sees it.
    public long MonotonicMs => Environment.TickCount64;

    private TimeSpan Offset()
    {
        if (_offsetFile is null)
        {
            return TimeSpan.Zero;
        }
        try
        {
            _last = TimeSpan.FromSeconds(double.Parse(File.ReadAllText(_offsetFile).Trim(), CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            _last = TimeSpan.Zero;
        }
        // The test may be rewriting the file, so the last value read stands.
        catch (Exception e) when (e is IOException or FormatException)
        {
        }
        return _last;
    }
}
