using System.Text;
using AgentContinuum.Core.Model;
using AgentContinuum.Core.State;

namespace AgentContinuum.Cli.Commands;

public static class StatusCommand
{
    // Only the label is free text; paths and anything from the prompt or the run stay out of the output.
    public static int Run(AppServices services)
    {
        using var store = services.OpenStore();
        var tasks = new TaskRepository(store).List();
        if (tasks.Count == 0)
        {
            services.Out.WriteLine("no tasks");
            return 0;
        }
        var now = services.Clock.UtcNow;
        foreach (var task in tasks)
        {
            var line = new StringBuilder($"{task.TaskId}  {task.Label}  {StateText.ToText(task.State)}");
            line.Append($"  attempts {task.AttemptsUsed}/{task.MaxAttempts}");
            if (task.State == TaskState.WaitingForReset)
            {
                var permitted = task.AttemptPermittedAtEpoch is { } epoch ? DateTimeOffset.FromUnixTimeSeconds(epoch) : now;
                line.Append(permitted > now ? $"  offer in {Countdown(permitted - now)}" : "  offer due");
            }
            if (task.State is not (TaskState.Completed or TaskState.Revoked))
            {
                line.Append(task.DeadlineUtc > now ? $"  deadline in {Countdown(task.DeadlineUtc - now)}" : "  deadline passed");
            }
            if (task.State == TaskState.NeedsAttention && task.AttentionReason is { } reason)
            {
                line.Append($"  reason: {reason}");
            }
            services.Out.WriteLine(line.ToString());
        }
        return 0;
    }

    private static string Countdown(TimeSpan left) =>
        left.TotalDays >= 1 ? $"{(int)left.TotalDays}d {left.Hours}h" : $"{(int)left.TotalHours}h {left.Minutes:00}m";
}
