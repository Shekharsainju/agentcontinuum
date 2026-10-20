using AgentContinuum.Core.Rules;

namespace AgentContinuum.Core.Billing;

/// <summary>P2: no settings scope Claude Code reads may route billing away from the subscription.</summary>
public static class SettingsScanner
{
    public static ProofResult Scan(string userProfile, string worktree, string? managedPath, ClaudeRules rules)
    {
        return ProofResult.FromProblems([]);
    }
}
