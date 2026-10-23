using AgentContinuum.Core.Model;
using AgentContinuum.Core.State;

namespace AgentContinuum.Core.Preflight.Checks;

internal sealed class AdmissionCheck : IPreflightCheck
{
    private readonly LaunchLedger _ledger;

    public AdmissionCheck(LaunchLedger ledger)
    {
        _ledger = ledger;
    }

    public IEnumerable<string> Reasons(PreflightContext ctx)
    {
        var task = ctx.Task;
        // A live op means an earlier launch is not yet recovered; reporting it beats BeginIntent throwing on the index.
        if (!LaunchPolicy.IsLaunchable(task.State) || _ledger.Live(task.TaskId) is not null)
        {
            yield return PreflightReasons.AdmissionState;
        }
        if (task.AttemptsUsed >= task.MaxAttempts)
        {
            yield return PreflightReasons.AttemptsExhausted;
        }
        if (ctx.UtcNow >= task.DeadlineUtc)
        {
            yield return PreflightReasons.DeadlinePassed;
        }
        if (!PromptInsideWorktree(task))
        {
            yield return PreflightReasons.PromptInvalid;
        }
    }

    // A link is refused outright: following it to prove its target is inside the worktree is not worth the risk.
    private static bool PromptInsideWorktree(TaskRecord task)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(task.Worktree));
            var prompt = Path.GetFullPath(Path.Combine(root, task.PromptFile));
            if (!prompt.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            var info = new FileInfo(prompt);
            return info.LinkTarget is null && info.Exists && info.Length > 0;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
