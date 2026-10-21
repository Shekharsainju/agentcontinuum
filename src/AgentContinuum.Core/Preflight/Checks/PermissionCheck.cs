using System.Text.Json;
using AgentContinuum.Core.Billing;
using AgentContinuum.Core.Model;

namespace AgentContinuum.Core.Preflight.Checks;

internal sealed class PermissionCheck : IPreflightCheck
{
    private const string Bypass = "bypassPermissions";

    private readonly PreflightHost _host;

    public PermissionCheck(PreflightHost host)
    {
        _host = host;
    }

    public IEnumerable<string> Reasons(PreflightContext ctx)
    {
        if (!LaunchPolicy.IsAllowedMode(ctx.Task.PermissionMode))
        {
            yield return $"{PreflightReasons.PermissionUnsafe}: policy mode not allowed";
        }
        foreach (var (scope, path) in SettingsScopes.All(_host.UserProfile, ctx.Task.Worktree, _host.ManagedSettingsPaths))
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                continue;
            }
            if (JsonPaths.ReadObject(path) is not { } doc)
            {
                yield return $"{PreflightReasons.PermissionUnsafe}: {scope} unreadable";
                continue;
            }
            using (doc)
            {
                if (NamesBypass(doc.RootElement))
                {
                    yield return $"{PreflightReasons.PermissionUnsafe}: {scope} sets {Bypass}";
                }
            }
        }
    }

    // Any string value naming the mode counts, not only permissions.defaultMode: a key added in a later CLI version
    // could switch it on, and refusing a settings file that merely mentions it costs one edit.
    private static bool NamesBypass(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => string.Equals(element.GetString(), Bypass, StringComparison.OrdinalIgnoreCase),
        JsonValueKind.Object => element.EnumerateObject().Any(p => NamesBypass(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(NamesBypass),
        _ => false,
    };
}
