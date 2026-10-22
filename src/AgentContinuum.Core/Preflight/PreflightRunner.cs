using AgentContinuum.Core.Billing;
using AgentContinuum.Core.Platform;
using AgentContinuum.Core.Preflight.Checks;
using AgentContinuum.Core.Rules;
using AgentContinuum.Core.State;

namespace AgentContinuum.Core.Preflight;

/// <summary>Runs every check and reports every failure. Never changes task or launch state.</summary>
public sealed class PreflightRunner
{
    private readonly TaskRepository _tasks;
    private readonly IClock _clock;
    private readonly PreflightHost _host;
    private readonly IReadOnlyList<IPreflightCheck> _checks;

    public PreflightRunner(
        StateStore store,
        IProcessLister lister,
        ICommandRunner runner,
        BillingProofs billing,
        IClock clock,
        PreflightHost host,
        Func<string, ClaudeRules?>? rulesFor = null)
    {
        _tasks = new TaskRepository(store);
        _clock = clock;
        _host = host;
        _checks =
        [
            new AdmissionCheck(new LaunchLedger(store)),
            new EligibilityCheck(),
            new ProcessCheck(lister),
            new BinaryCheck(runner),
            new WorkspaceCheck(host),
            new PermissionCheck(host),
            new BillingCheck(billing, host, rulesFor ?? VersionRules.For),
            new ControllerCheck(store),
        ];
    }

    public PreflightResult Run(string taskId, bool now)
    {
        if (_tasks.Get(taskId) is not { } task)
        {
            return new PreflightResult(false, [PreflightReasons.AdmissionState]);
        }

        var ctx = new PreflightContext(task, now, _clock.UtcNow, BuildChildEnv(task.CliPath), BinaryCheck.PinProblem(task));
        var reasons = _checks.SelectMany(check => check.Reasons(ctx)).Distinct().ToList();
        return new PreflightResult(reasons.Count == 0, reasons);
    }

    private IReadOnlyDictionary<string, string>? BuildChildEnv(string cliPath)
    {
        try
        {
            return ChildEnvironment.Build(_host.ParentEnv, Path.GetDirectoryName(cliPath) ?? "", Path.GetDirectoryName(_host.GitExe) ?? "");
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
