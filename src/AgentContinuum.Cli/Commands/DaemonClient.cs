using System.Text.Json.Nodes;
using AgentContinuum.Core.State;

namespace AgentContinuum.Cli.Commands;

/// <summary>Sends one request to the daemon and waits for its reply.</summary>
public static class DaemonClient
{
    public const int NotRunning = 3;

    public const int NoReply = 4;

    public static (int Exit, JsonObject? Reply) Send(AppServices services, string kind, string? taskId, JsonObject args)
    {
        using var store = services.OpenStore();
        var queue = new RequestQueue(store, services.Clock);
        var id = queue.Submit(kind, taskId, args.ToJsonString());
        var reply = queue.WaitFor(id, services.ClientWait);
        // A request nobody took in time is withdrawn, so a daemon started later never acts on it. Once taken, the
        // daemon is alive and running pre-flight, which can outlast the first wait.
        if (reply is null && queue.Withdraw(id))
        {
            services.Out.WriteLine("daemon not running");
            return (NotRunning, null);
        }
        reply ??= queue.WaitFor(id, services.ReplyWait);
        if (reply is null)
        {
            services.Out.WriteLine("no reply from daemon");
            return (NoReply, null);
        }
        return (0, JsonNode.Parse(reply)!.AsObject());
    }
}
