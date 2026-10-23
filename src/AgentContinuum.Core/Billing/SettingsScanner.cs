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

    /// <summary>Both locations Claude Code has used for managed settings on Windows; which one 2.1.304 reads is unverified.</summary>
    public static IReadOnlyList<string> DefaultManagedPaths() =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClaudeCode", "managed-settings.json"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ClaudeCode", "managed-settings.json"),
    ];

    public static ProofResult Scan(string userProfile, string worktree, IReadOnlyList<string> managedPaths, ClaudeRules? rules)
    {
        var problems = new List<string>();
        // With no managed path the managed scope would go unscanned, and an unavailable special folder leaves a relative
        // path that names some other file; neither may read as a pass.
        if (managedPaths.Count == 0 || managedPaths.Any(p => string.IsNullOrEmpty(p) || !Path.IsPathFullyQualified(p)))
        {
            problems.Add("managed: no path");
        }
        foreach (var (scope, path) in SettingsScopes.All(userProfile, worktree, managedPaths))
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                continue;
            }

            // A file Claude Code might read but this scan cannot is a failure, never a pass.
            if (JsonPaths.ReadObject(path) is not { } doc)
            {
                problems.Add($"{scope}: unreadable");
                continue;
            }

            using (doc)
            {
                CheckSettings(scope, doc.RootElement, rules, problems);
            }
        }
        return ProofResult.FromProblems(problems);
    }

    private static void CheckSettings(string scope, JsonElement root, ClaudeRules? rules, List<string> problems)
    {
        if (root.TryGetProperty("apiKeyHelper", out _))
        {
            problems.Add($"{scope}: apiKeyHelper");
        }
        if (root.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
        {
            foreach (var variable in env.EnumerateObject())
            {
                // Claude Code adds providers as CLAUDE_CODE_USE_<name>; an unknown one is still a route off the subscription.
                if (BillingVariables.Contains(variable.Name, StringComparer.OrdinalIgnoreCase)
                    || variable.Name.StartsWith(ProviderSwitchPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{scope}: env.{variable.Name.ToUpperInvariant()}");
                }
            }
        }
        foreach (var key in rules?.P2ExtraKeys ?? [])
        {
            var problem = $"{scope}: {key}";
            if (!problems.Contains(problem)
                && (key.StartsWith(EnvPrefix, StringComparison.Ordinal) ? HasEnvName(root, key[EnvPrefix.Length..]) : JsonPaths.Has(root, key)))
            {
                problems.Add(problem);
            }
        }
    }

    private const string EnvPrefix = "env.";

    private const string ProviderSwitchPrefix = "CLAUDE_CODE_USE_";

    // Windows environment names are case-insensitive, so Claude Code honours an env entry in any case.
    private static bool HasEnvName(JsonElement root, string name) =>
        root.TryGetProperty("env", out var env)
        && env.ValueKind == JsonValueKind.Object
        && env.EnumerateObject().Any(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
