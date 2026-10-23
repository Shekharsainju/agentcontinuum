using AgentContinuum.Core.Platform;

namespace AgentContinuum.Core.Tests;

public sealed class RecordingLog : ILog
{
    private readonly List<string> _messages = [];

    // Launch threads and the daemon loop write at once.
    public List<string> Messages
    {
        get
        {
            lock (_messages)
            {
                return [.. _messages];
            }
        }
    }

    public void Write(string message)
    {
        lock (_messages)
        {
            _messages.Add(message);
        }
    }
}
