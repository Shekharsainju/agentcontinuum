using AgentContinuum.Core.Schedule;

namespace AgentContinuum.Windows.Tests;

public sealed class ToastTextTests
{
    [Theory]
    [InlineData(false, false, "fix tests: usage limit has reset")]
    [InlineData(true, false, "fix tests: usage limit hit, reset time unknown")]
    [InlineData(false, true, "fix tests: the launch did not start")]
    public void OfferText_NamesLabelAndFixedText(bool resetUnknown, bool didNotStart, string expected)
    {
        Assert.Equal([expected], ToastNotifier.OfferText(new Offer("t1", "fix tests", resetUnknown, didNotStart)));
    }

    [Fact]
    public void AttentionText_NamesLabelAndReason()
    {
        Assert.Equal(["fix tests needs attention", "Interrupted run"], ToastNotifier.AttentionText("fix tests", "Interrupted run"));
    }
}
