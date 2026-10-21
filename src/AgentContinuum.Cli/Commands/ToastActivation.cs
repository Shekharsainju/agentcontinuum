using System.Text.Json.Nodes;
using AgentContinuum.Core.State;

namespace AgentContinuum.Cli.Commands;

/// <summary>Turns a notification button click into a request for the daemon.</summary>
public static class ToastActivation
{
    // The click comes from outside the process, so only the exact form the notifier writes is accepted.
    public static void Handle(string arguments, AppServices services)
    {
        var parts = arguments.Split('&');
        if (parts is not [var action, var task] || action != "action=resume" || !task.StartsWith("task=", StringComparison.Ordinal)
            || task.Length == "task=".Length)
        {
            services.Log.Write("ignored a notification click");
            return;
        }
        var taskId = task["task=".Length..];
        try
        {
            using var store = services.OpenStore();
            var args = new JsonObject { ["now"] = false }.ToJsonString();
            var id = new RequestQueue(store, services.Clock).Submit(RequestKinds.Resume, taskId, args);
            services.Log.Write($"resume click for task {taskId} sent as request {id}");
        }
        catch (Exception e)
        {
            services.Log.Write($"resume click for task {taskId} not sent: {e.GetType().Name}");
        }
    }
}
