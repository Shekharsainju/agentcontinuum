using System.Text.Json.Nodes;
using AgentContinuum.Core.State;

namespace AgentContinuum.Cli.Commands;

/// <summary>Sends one request to the daemon and waits for its reply.</summary>
public static class DaemonClient
{
    public const int NoReply = 4;

    public static (int Exit, JsonObject? Reply) Send(AppServices services, string kind, string? taskId, JsonObject args)
    {
        using var store = services.OpenStore();
        var queue = new RequestQueue(store, services.Clock);
        var id = queue.Submit(kind, taskId, args.ToJsonString());
        if (queue.WaitFor(id, services.ReplyWait) is not { } reply)
        {
            services.Out.WriteLine("no reply from daemon");
            return (NoReply, null);
        }
        return (0, JsonNode.Parse(reply)!.AsObject());
    }
}
