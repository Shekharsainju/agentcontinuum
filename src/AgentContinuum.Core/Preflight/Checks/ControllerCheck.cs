using AgentContinuum.Core.State;

namespace AgentContinuum.Core.Preflight.Checks;

internal sealed class ControllerCheck : IPreflightCheck
{
    private readonly StateStore _store;

    public ControllerCheck(StateStore store)
    {
        _store = store;
    }

    public IEnumerable<string> Reasons(PreflightContext ctx)
    {
        if (!_store.IntegrityOk() || _store.SchemaVersion != StateStore.CurrentSchemaVersion)
        {
            yield return PreflightReasons.ControllerState;
        }
    }
}
