namespace AgentContinuum.Cli.Commands;

/// <summary><c>--name value</c> pairs; a name may repeat only when it is listed as repeatable.</summary>
public sealed class Options
{
    private readonly Dictionary<string, List<string>> _values;

    private Options(Dictionary<string, List<string>> values)
    {
        _values = values;
    }

    public static Options? Parse(IReadOnlyList<string> args, IReadOnlyCollection<string> names, IReadOnlyCollection<string> repeatable)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i += 2)
        {
            var name = args[i];
            if (!names.Contains(name) || i + 1 >= args.Count)
            {
                return null;
            }
            if (values.TryGetValue(name, out var list) && !repeatable.Contains(name))
            {
                return null;
            }
            (values[name] = list ?? []).Add(args[i + 1]);
        }
        return new Options(values);
    }

    public string? One(string name) => _values.TryGetValue(name, out var list) ? list[0] : null;

    public IReadOnlyList<string> All(string name) => _values.TryGetValue(name, out var list) ? list : [];
}
