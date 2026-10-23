using System.Text.Json.Nodes;

namespace AgentContinuum.Cli.Commands;

public static class LaunchCommands
{
    public static int Start(AppServices services, string taskId) =>
        Launch(services, RequestKinds.Start, taskId, new JsonObject());

    public static int Resume(AppServices services, string taskId, bool now) =>
        Launch(services, RequestKinds.Resume, taskId, new JsonObject { ["now"] = now });

    public static int Revoke(AppServices services, string taskId)
    {
        var (exit, reply) = DaemonClient.Send(services, RequestKinds.Revoke, taskId, new JsonObject());
        if (reply is null)
        {
            return exit;
        }
        if (reply["error"] is { } error)
        {
            services.Out.WriteLine($"error: {error}");
            return 1;
        }
        services.Out.WriteLine("revoked");
        return 0;
    }

    private static int Launch(AppServices services, string kind, string taskId, JsonObject args)
    {
        var (exit, reply) = DaemonClient.Send(services, kind, taskId, args);
        if (reply is null)
        {
            return exit;
        }
        if (reply["error"] is { } error)
        {
            services.Out.WriteLine($"error: {error}");
            return 1;
        }
        if (reply["launched"]?.GetValue<bool>() == true)
        {
            services.Out.WriteLine($"launched op {reply["op"]}");
            return 0;
        }
        var reasons = reply["reasons"]?.AsArray().Select(r => r!.GetValue<string>()) ?? [];
        services.Out.WriteLine($"refused: {string.Join(", ", reasons)}");
        return 1;
    }
}
