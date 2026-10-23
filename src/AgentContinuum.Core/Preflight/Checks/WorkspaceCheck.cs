using AgentContinuum.Core.Workspace;

namespace AgentContinuum.Core.Preflight.Checks;

/// <summary>A first launch has no baseline, but its fingerprint must still be computable: the post-exit snapshot needs it.</summary>
internal sealed class WorkspaceCheck : IPreflightCheck
{
    private readonly PreflightHost _host;

    public WorkspaceCheck(PreflightHost host)
    {
        _host = host;
    }

    public IEnumerable<string> Reasons(PreflightContext ctx)
    {
        var result = WorkspaceFingerprint.Compute(ctx.Task.Worktree, _host.GitExe, _host.FingerprintMaxFiles);
        if (result.Incomplete)
        {
            return [PreflightReasons.WorkspaceTooLarge];
        }
        if (result.Error is { } error)
        {
            return [$"{PreflightReasons.WorkspaceChanged}: fingerprint failed, {error}"];
        }
        if (ctx.Task.BaselineFingerprint is { } baseline && baseline != result.Hash)
        {
            return [PreflightReasons.WorkspaceChanged];
        }
        return [];
    }
}
