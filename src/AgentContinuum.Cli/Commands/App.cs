namespace AgentContinuum.Cli.Commands;

public static class App
{
    public const int UsageError = 64;

    public static async Task<int> Run(string[] args, AppServices services)
    {
        switch (args)
        {
            case ["daemon"]:
                return await RunDaemon(services);
            case ["task", "add", .. var addArgs]:
                return TaskCommands.Add(services, addArgs);
            case ["pin-cli", var taskId, .. var pinArgs]:
                return LocalCommands.PinCli(services, taskId, pinArgs);
            case ["accept-workspace", var taskId]:
                return LocalCommands.AcceptWorkspace(services, taskId);
            case ["attest-overage-off"]:
                return LocalCommands.AttestOverageOff(services);
            case ["preview", var taskId]:
                return LocalCommands.Preview(services, taskId, now: false);
            case ["preview", var taskId, "--now"]:
                return LocalCommands.Preview(services, taskId, now: true);
            case ["status"]:
                return StatusCommand.Run(services);
            case ["task", "start", var taskId]:
                return LaunchCommands.Start(services, taskId);
            case ["revoke", var taskId]:
                return LaunchCommands.Revoke(services, taskId);
            case ["resume", var taskId]:
                return LaunchCommands.Resume(services, taskId, now: false);
            case ["resume", var taskId, "--now"]:
                return LaunchCommands.Resume(services, taskId, now: true);
            default:
                return Usage(services);
        }
    }

    private static async Task<int> RunDaemon(AppServices services)
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        using var daemon = new Daemon(services);
        return await daemon.Run(stop.Token);
    }

    private static int Usage(AppServices services)
    {
        services.Out.WriteLine("usage: ac <daemon|task add|task start|resume|revoke|status|preview|pin-cli|accept-workspace|attest-overage-off>");
        return UsageError;
    }
}
