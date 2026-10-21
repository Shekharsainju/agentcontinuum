using AgentContinuum.Cli.Commands;
using AgentContinuum.Core.Model;
using AgentContinuum.Core.Tests;
using AgentContinuum.Core.Tests.Preflight;

namespace AgentContinuum.Cli.Tests;

public sealed class TaskAddTests : IDisposable
{
    private readonly CliFixture _c = new();

    public void Dispose() => _c.Dispose();

    private string[] AddArgs(string cli) =>
    [
        "task", "add", "--label", "second", "--worktree", _c.F.Repo.Path, "--prompt-file", "prompt.md",
        "--permission-mode", "plan", "--allowed-tools", "Read", "--allowed-tools", "Bash(git diff:*,stat)",
        "--max-attempts", "3", "--deadline", "2026-10-27T12:00:00Z", "--cli", cli,
    ];

    [Fact]
    public async Task TaskAdd_PinsCliAndQueuesTask()
    {
        var services = _c.Services();

        var exit = await App.Run(AddArgs(FakeClaudeProcess.ExePath), services);

        Assert.Equal(0, exit);
        var id = _c.Output.Trim();
        var task = _c.F.Tasks.Get(id)!;
        Assert.Equal(
            ("second", "prompt.md", "plan", 3, TaskState.Queued, 0),
            (task.Label, task.PromptFile, task.PermissionMode, task.MaxAttempts, task.State, task.AttemptsUsed));
        Assert.Equal(["Read", "Bash(git diff:*,stat)"], task.AllowedTools);
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero), task.DeadlineUtc);
        Assert.Equal(
            (FakeClaudeProcess.ExePath, PreflightFixture.Sha256(FakeClaudeProcess.ExePath), "2.1.304"),
            (task.CliPath, task.CliSha256, task.CliVersion));
        Assert.NotEqual(Guid.Empty, task.SessionId);
        Assert.NotEqual(_c.F.Task.SessionId, task.SessionId);
    }

    [Fact]
    public async Task TaskAdd_RejectsNonExeCli()
    {
        var services = _c.Services();
        var shim = _c.F.Outside("claude.cmd");
        File.WriteAllText(shim, "@echo off");

        var exit = await App.Run(AddArgs(shim), services);

        Assert.Equal(1, exit);
        Assert.Equal("error: cli must be an .exe file", _c.Output.Trim());
        Assert.Single(_c.F.Tasks.List());
    }

    [Theory]
    [InlineData("--permission-mode", "bypassPermissions", "error: permission mode must be default, acceptEdits or plan")]
    [InlineData("--prompt-file", "..\\outside.md", "error: prompt file must be a non-empty file inside the worktree")]
    [InlineData("--max-attempts", "0", "error: max attempts must be a whole number from 1 to 100")]
    [InlineData("--deadline", "2026-10-19T00:00:00Z", "error: deadline must be a future UTC time")]
    public async Task TaskAdd_RejectsInvalidValues(string option, string value, string expected)
    {
        var services = _c.Services();
        var args = AddArgs(FakeClaudeProcess.ExePath);
        args[Array.IndexOf(args, option) + 1] = value;

        var exit = await App.Run(args, services);

        Assert.Equal(1, exit);
        Assert.Equal(expected, _c.Output.Trim());
        Assert.Single(_c.F.Tasks.List());
    }
}
