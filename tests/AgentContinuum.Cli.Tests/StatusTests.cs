using AgentContinuum.Cli.Commands;
using AgentContinuum.Core.Model;

namespace AgentContinuum.Cli.Tests;

public sealed class StatusTests : IDisposable
{
    private readonly CliFixture _c = new();

    public void Dispose() => _c.Dispose();

    [Fact]
    public async Task Status_ShowsCountdownsNoContent()
    {
        var now = _c.F.Clock.UtcNow;
        _c.F.Task = _c.F.Task with
        {
            State = TaskState.WaitingForReset,
            AttemptsUsed = 1,
            AttemptPermittedAtEpoch = now.AddMinutes(65).ToUnixTimeSeconds(),
        };
        var services = _c.Services();
        _c.F.Tasks.Add(_c.F.Task with
        {
            TaskId = "t2",
            Label = "other",
            State = TaskState.NeedsAttention,
            AttemptsUsed = 3,
            AttemptPermittedAtEpoch = null,
            AttentionReason = "Attempts exhausted",
            DeadlineUtc = now.AddHours(5),
        });

        var exit = await App.Run(["status"], services);

        Assert.Equal(0, exit);
        Assert.Equal(
            [
                "t1  sample  WAITING_FOR_RESET  attempts 1/3  offer in 1h 05m  deadline in 7d 0h",
                "t2  other  NEEDS_ATTENTION  attempts 3/3  deadline in 5h 00m  reason: Attempts exhausted",
            ],
            _c.Output.TrimEnd().Split(Environment.NewLine));
        Assert.DoesNotContain("SENTINEL", _c.Output);
        Assert.DoesNotContain("prompt", _c.Output);
    }

    [Fact]
    public async Task Status_NoTasks()
    {
        var services = _c.Services();
        using (var cmd = _c.F.Store.Connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM tasks";
            cmd.ExecuteNonQuery();
        }

        await App.Run(["status"], services);

        Assert.Equal("no tasks", _c.Output.Trim());
    }
}
