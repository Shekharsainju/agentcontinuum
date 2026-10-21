using AgentContinuum.Cli.Commands;
using AgentContinuum.Core.State;

namespace AgentContinuum.Cli.Tests;

public sealed class ToastActivationTests : IDisposable
{
    private readonly CliFixture _c = new();

    public void Dispose() => _c.Dispose();

    [Fact]
    public void ToastResume_SubmitsResumeRequest()
    {
        var services = _c.Services();

        ToastActivation.Handle(Windows.ToastNotifier.ResumeArguments(CliFixture.TaskId), services);

        var request = new RequestQueue(_c.F.Store, _c.F.Clock).Next()!;
        Assert.Equal((RequestKinds.Resume, CliFixture.TaskId, """{"now":false}"""), (request.Kind, request.TaskId, request.ArgsJson));
    }

    [Theory]
    [InlineData("action=dismiss&task=t1")]
    [InlineData("action=resume&task=")]
    [InlineData("action=resume")]
    [InlineData("")]
    public void OtherArguments_SubmitNothing(string arguments)
    {
        var services = _c.Services();

        ToastActivation.Handle(arguments, services);

        Assert.Null(new RequestQueue(_c.F.Store, _c.F.Clock).Next());
        Assert.Contains("ignored a notification click", _c.Log.Messages);
    }
}
