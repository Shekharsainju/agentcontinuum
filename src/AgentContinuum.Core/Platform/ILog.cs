namespace AgentContinuum.Core.Platform;

/// <summary>Controller log. Messages carry IDs and codes only, never task content.</summary>
public interface ILog
{
    void Write(string message);
}
