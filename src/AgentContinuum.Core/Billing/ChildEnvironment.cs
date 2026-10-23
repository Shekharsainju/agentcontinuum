namespace AgentContinuum.Core.Billing;

/// <summary>P1: the child environment is built from an allowlist, so no billing variable can be inherited.</summary>
public static class ChildEnvironment
{
    public const string PathName = "PATH";

    private static readonly string[] Inherited =
    [
        "SystemRoot", "windir", "ComSpec", "PATHEXT", "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
        "HOMEDRIVE", "HOMEPATH", "USERNAME", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "OS",
    ];

    public static IReadOnlyDictionary<string, string> Build(IReadOnlyDictionary<string, string> parent, string cliDir, string gitDir)
    {
        // Windows variable names are case-insensitive, so look up by name regardless of the parent's casing.
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in parent)
        {
            lookup[name] = value;
        }

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Inherited)
        {
            if (lookup.TryGetValue(name, out var value))
            {
                env[name] = value;
            }
        }

        var systemRoot = lookup.GetValueOrDefault("SystemRoot")
            ?? throw new InvalidOperationException("SystemRoot is not set; cannot construct PATH.");
        env[PathName] = string.Join(';', Path.Combine(systemRoot, "System32"), systemRoot, cliDir, gitDir);
        return env;
    }

    public static bool IsAllowlisted(IReadOnlyDictionary<string, string> env) =>
        env.Keys.All(name => name.Equals(PathName, StringComparison.OrdinalIgnoreCase)
            || Inherited.Contains(name, StringComparer.OrdinalIgnoreCase));
}
