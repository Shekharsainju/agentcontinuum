using AgentContinuum.Core.Model;
using AgentContinuum.Core.State;

namespace AgentContinuum.Core.Tests.State;

public sealed class StateTextTests
{
    [Fact]
    public void ToText_WritesUpperSnake()
    {
        Assert.Equal("WAITING_FOR_RESET", StateText.ToText(TaskState.WaitingForReset));
        Assert.Equal("INTENT", StateText.ToText(OpState.Intent));
        Assert.Equal("BILLING_MISMATCH", StateText.ToText(Outcome.BillingMismatch));
    }

    [Fact]
    public void Parse_RoundTripsEveryValue()
    {
        foreach (var value in Enum.GetValues<TaskState>())
        {
            Assert.Equal(value, StateText.Parse<TaskState>(StateText.ToText(value)));
        }
        foreach (var value in Enum.GetValues<OpState>())
        {
            Assert.Equal(value, StateText.Parse<OpState>(StateText.ToText(value)));
        }
        foreach (var value in Enum.GetValues<Outcome>())
        {
            Assert.Equal(value, StateText.Parse<Outcome>(StateText.ToText(value)));
        }
    }

    [Fact]
    public void Parse_UnknownText_Throws()
    {
        Assert.Throws<FormatException>(() => StateText.Parse<TaskState>("WaitingForReset"));
    }
}
