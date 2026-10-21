using AgentContinuum.Core.Model;

namespace AgentContinuum.Core.Preflight.Checks;

internal interface IPreflightCheck
{
    IEnumerable<string> Reasons(PreflightContext ctx);
}

/// <summary>
/// <see cref="ChildEnv"/> is null when the parent environment cannot yield one. <see cref="CliPinProblem"/> is null only
/// when the CLI is the pinned file, and any check that runs it must check that first.
/// </summary>
internal sealed record PreflightContext(
    TaskRecord Task,
    bool Now,
    DateTimeOffset UtcNow,
    IReadOnlyDictionary<string, string>? ChildEnv,
    string? CliPinProblem);
