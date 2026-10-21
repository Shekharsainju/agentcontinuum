using AgentContinuum.Core.State;

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

    private static int NoSuchTask(AppServices services)
    {
        services.Out.WriteLine("error: no such task");
        return 1;
    }
}
