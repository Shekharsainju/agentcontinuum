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
