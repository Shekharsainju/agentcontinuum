using AgentContinuum.Core.Platform;

namespace AgentContinuum.Core.Tests;

public sealed class RecordingLog : ILog
{
    public List<string> Messages { get; } = [];

    public void Write(string message) => Messages.Add(message);
}
