using AgentContinuum.Core.Model;
using AgentContinuum.Core.Rules;

namespace AgentContinuum.Core.Billing;

public sealed record BillingContext(
    TaskRecord Task,
    ClaudeRules? Rules,
    IReadOnlyDictionary<string, string> ChildEnv,
    string UserProfile,
    string? ManagedSettingsPath);

/// <summary><see cref="FailedProofs"/> holds proof IDs such as P2 or P4_UNEVALUABLE, never values.</summary>
public sealed record BillingVerdict(bool Verified, IReadOnlyList<string> FailedProofs);
