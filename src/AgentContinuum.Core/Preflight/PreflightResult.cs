using AgentContinuum.Core.Workspace;

namespace AgentContinuum.Core.Preflight;

/// <summary><see cref="Reasons"/> hold a spec reason code, sometimes followed by ": " and a detail that names no content.</summary>
public sealed record PreflightResult(bool Pass, IReadOnlyList<string> Reasons);

public sealed record PreflightHost(
    string GitExe,
    IReadOnlyDictionary<string, string> ParentEnv,
    string UserProfile,
    IReadOnlyList<string> ManagedSettingsPaths,
    int FingerprintMaxFiles = WorkspaceFingerprint.MaxFiles);

public static class PreflightReasons
{
    public const string AdmissionState = "ADMISSION_STATE";
    public const string AttemptsExhausted = "ATTEMPTS_EXHAUSTED";
    public const string DeadlinePassed = "DEADLINE_PASSED";
    public const string PromptInvalid = "PROMPT_INVALID";
    public const string NotYetEligible = "NOT_YET_ELIGIBLE";
    public const string ProcessLive = "PROCESS_LIVE";
    public const string ProcessUnknown = "PROCESS_UNKNOWN";
    public const string CliChanged = "CLI_CHANGED";
    public const string WorkspaceChanged = "WORKSPACE_CHANGED";
    public const string WorkspaceTooLarge = "WORKSPACE_TOO_LARGE";
    public const string PermissionUnsafe = "PERMISSION_UNSAFE";
    public const string BillingUnverified = "BILLING_UNVERIFIED";
    public const string ControllerState = "CONTROLLER_STATE";
}
