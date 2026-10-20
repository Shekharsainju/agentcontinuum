using AgentContinuum.Core.Model;

namespace AgentContinuum.Core.Preflight.Checks;

internal interface IPreflightCheck
{
    IEnumerable<string> Reasons(PreflightContext ctx);
}

/// <summary><see cref="ChildEnv"/> is null when the parent environment cannot yield one.</summary>
internal sealed record PreflightContext(TaskRecord Task, bool Now, DateTimeOffset UtcNow, IReadOnlyDictionary<string, string>? ChildEnv);
