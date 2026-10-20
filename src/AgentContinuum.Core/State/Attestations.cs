namespace AgentContinuum.Core.State;

public sealed class Attestations
{
    public const string OverageOff = "overage_off";

    private readonly StateStore _store;

    public Attestations(StateStore store)
    {
        _store = store;
    }

    public void Record(string kind, DateTimeOffset at)
    {
        using var cmd = _store.Connection.CreateCommand();
        cmd.CommandText = "INSERT INTO attestations (kind, at_utc) VALUES ($kind, $at)";
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$at", at.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    public DateTimeOffset? Latest(string kind)
    {
        using var cmd = _store.Connection.CreateCommand();
        cmd.CommandText = "SELECT max(at_utc) FROM attestations WHERE kind = $kind";
        cmd.Parameters.AddWithValue("$kind", kind);
        return cmd.ExecuteScalar() is long at ? DateTimeOffset.FromUnixTimeSeconds(at) : null;
    }
}
