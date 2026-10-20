using AgentContinuum.Core.Model;

namespace AgentContinuum.Core.Classify;

public sealed record Classification(Outcome Outcome, string Reason, long? ResetEpoch);
