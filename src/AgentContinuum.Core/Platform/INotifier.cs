using AgentContinuum.Core.Schedule;

namespace AgentContinuum.Core.Platform;

/// <summary>Only the label is free text; nothing from the prompt or the run is ever shown.</summary>
public interface INotifier
{
    void OfferResume(Offer offer);

    void Attention(string taskId, string label, string reasonCode);
}
