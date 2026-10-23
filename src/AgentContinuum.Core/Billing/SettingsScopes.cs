namespace AgentContinuum.Core.Billing;

/// <summary>Every settings file Claude Code reads for a launch, as (scope, path).</summary>
public static class SettingsScopes
{
    public static IEnumerable<(string Scope, string Path)> All(string userProfile, string worktree, IReadOnlyList<string> managedPaths)
    {
        foreach (var managedPath in managedPaths)
        {
            yield return ("managed", managedPath);
        }
        yield return ("user", Path.Combine(userProfile, ".claude", "settings.json"));
        yield return ("user-local", Path.Combine(userProfile, ".claude", "settings.local.json"));
        yield return ("project", Path.Combine(worktree, ".claude", "settings.json"));
        yield return ("project-local", Path.Combine(worktree, ".claude", "settings.local.json"));
    }
}
