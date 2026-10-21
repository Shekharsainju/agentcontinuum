using System.Globalization;
using AgentContinuum.Core.Billing;
using AgentContinuum.Core.Model;
using AgentContinuum.Core.Preflight;
using AgentContinuum.Core.State;
using AgentContinuum.Core.Workspace;

namespace AgentContinuum.Cli.Commands;

/// <summary>Commands that change the state database directly, without the daemon.</summary>
public static class LocalCommands
{
    public static int PinCli(AppServices services, string taskId, IReadOnlyList<string> args)
    {
        if (Options.Parse(args, ["--cli"], []) is not { } options)
        {
            services.Out.WriteLine("usage: ac pin-cli <task> [--cli <claude.exe>]");
            return App.UsageError;
        }
        using var store = services.OpenStore();
        var tasks = new TaskRepository(store);
        if (tasks.Get(taskId) is not { } task)
        {
            return NoSuchTask(services);
        }
        var (pin, error) = CliPin.Read(services, options.One("--cli") ?? task.CliPath);
        if (pin is null)
        {
            services.Out.WriteLine($"error: {error}");
            return 1;
        }
        tasks.SetCliPin(taskId, pin.Path, pin.Sha256, pin.Version);
        services.Log.Write($"pinned task {taskId} to cli {pin.Version}");
        services.Out.WriteLine($"pinned {pin.Version}");
        return 0;
    }

    // A live launch may still be writing to the worktree, and a baseline taken now would hide that work.
    public static int AcceptWorkspace(AppServices services, string taskId)
    {
        using var store = services.OpenStore();
        var tasks = new TaskRepository(store);
        if (tasks.Get(taskId) is not { } task)
        {
            return NoSuchTask(services);
        }
        if (task.State == TaskState.Running || new LaunchLedger(store).Live(taskId) is not null)
        {
            services.Out.WriteLine("error: task has a live launch");
            return 1;
        }
        var result = WorkspaceFingerprint.Compute(task.Worktree, services.Preflight.GitExe, services.Preflight.FingerprintMaxFiles);
        if (result is not { Hash: { } hash, Incomplete: false })
        {
            services.Out.WriteLine(result.Incomplete ? "error: workspace too large" : "error: workspace could not be read");
            return 1;
        }
        tasks.SetBaseline(taskId, hash);
        services.Log.Write($"accepted workspace for task {taskId}");
        services.Out.WriteLine("baseline taken");
        return 0;
    }

    public static int AttestOverageOff(AppServices services)
    {
        var now = services.Clock.UtcNow;
        using var store = services.OpenStore();
        new Attestations(store).Record(Attestations.OverageOff, now);
        services.Log.Write("attested overage off");
        var until = (now + BillingProofs.AttestationMaxAge).UtcDateTime;
        services.Out.WriteLine($"overage off attested, valid until {until.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC");
        return 0;
    }

    // The same pre-flight a launch runs, which never changes task or launch state (R9).
    public static int Preview(AppServices services, string taskId, bool now)
    {
        using var store = services.OpenStore();
        var s = services;
        var runner = new PreflightRunner(store, s.Lister, s.Runner, new BillingProofs(s.Runner, s.Clock, store), s.Clock, s.Preflight);
        var result = runner.Run(taskId, now);
        services.Out.WriteLine(result.Pass ? "pre-flight passed" : $"pre-flight failed: {string.Join(", ", result.Reasons)}");
        return result.Pass ? 0 : 1;
    }

    private static int NoSuchTask(AppServices services)
    {
        services.Out.WriteLine("error: no such task");
        return 1;
    }
}
