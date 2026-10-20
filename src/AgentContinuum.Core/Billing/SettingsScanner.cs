using System.Text.Json;
using AgentContinuum.Core.Rules;

namespace AgentContinuum.Core.Billing;

/// <summary>P2: no settings scope Claude Code reads may route billing away from the subscription.</summary>
public static class SettingsScanner
{
    private static readonly string[] BillingVariables =
    [
        "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX",
    ];

    public static ProofResult Scan(string userProfile, string worktree, string? managedPath, ClaudeRules rules)
    {
        var problems = new List<string>();
        foreach (var (scope, path) in Scopes(userProfile, worktree, managedPath))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("apiKeyHelper", out _))
            {
                problems.Add($"{scope}: apiKeyHelper");
            }
            if (root.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
            {
                foreach (var variable in env.EnumerateObject())
                {
                    if (BillingVariables.Contains(variable.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        problems.Add($"{scope}: env.{variable.Name.ToUpperInvariant()}");
                    }
                }
            }
        }
        return ProofResult.FromProblems(problems);
    }

    private static IEnumerable<(string Scope, string Path)> Scopes(string userProfile, string worktree, string? managedPath)
    {
        if (managedPath is not null)
        {
            yield return ("managed", managedPath);
        }
        yield return ("user", Path.Combine(userProfile, ".claude", "settings.json"));
        yield return ("user-local", Path.Combine(userProfile, ".claude", "settings.local.json"));
        yield return ("project", Path.Combine(worktree, ".claude", "settings.json"));
        yield return ("project-local", Path.Combine(worktree, ".claude", "settings.local.json"));
    }
}
