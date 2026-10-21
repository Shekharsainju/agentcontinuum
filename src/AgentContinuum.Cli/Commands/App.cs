namespace AgentContinuum.Cli.Commands;

public static class App
{
    public const int UsageError = 64;

    public static async Task<int> Run(string[] args, AppServices services)
    {
        await Task.CompletedTask;
        return Usage(services);
    }

    private static int Usage(AppServices services)
    {
        services.Out.WriteLine("usage: ac <daemon|task add|task start|resume|revoke|status|preview|pin-cli|accept-workspace|attest-overage-off>");
        return UsageError;
    }
}
