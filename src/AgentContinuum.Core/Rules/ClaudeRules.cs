using System.Text.Json;

namespace AgentContinuum.Core.Rules;

public sealed record ClaudeRules(
    string Versions,
    string[] P2ExtraKeys,
    CredentialRule? P3,
    AuthStatusRule? P4,
    FirstEventRule? P5,
    ResultRule? Result,
    UsageRule? Usage,
    LimitSignalRule? Limit);

public sealed record FieldRequirement(string Path, JsonElement[] Values);

/// <summary>Credential files are relative to %USERPROFILE%.</summary>
public sealed record CredentialRule(string File, string[] SubscriptionKeys, string ApiKeyFile, string[] ApiKeyKeys);

public sealed record AuthStatusRule(string[] Args, FieldRequirement[] Require, string[] RequirePresent);

/// <summary>Matched on type and subtype, never on line position.</summary>
public sealed record FirstEventRule(string Type, string Subtype, string Field, string[] SubscriptionValues);

public sealed record ResultRule(string Type, FieldRequirement[] SuccessWhen);

public sealed record UsageRule(string Type, string ResetField, string ResetFormat, string[] UtilizationFields, OverageRule Overage);

public sealed record OverageRule(string StatusField, string InUseField, JsonElement[] SafeInUse);

public sealed record LimitSignalRule(string Hook, string ReasonField, string[] ReasonValues, string? ResetField, string? ResetFormat);
