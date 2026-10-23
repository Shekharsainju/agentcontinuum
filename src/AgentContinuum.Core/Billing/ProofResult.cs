namespace AgentContinuum.Core.Billing;

/// <summary><see cref="Problems"/> name scopes and keys only, never values.</summary>
public sealed record ProofResult(bool Pass, IReadOnlyList<string> Problems)
{
    public static ProofResult FromProblems(IReadOnlyList<string> problems) => new(problems.Count == 0, problems);
}
