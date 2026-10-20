using System.Text;
using System.Text.Json;
using AgentContinuum.Core.Rules;

namespace AgentContinuum.Core.Events;

public sealed class EventFilter
{
    public const int MaxLineBytes = 1024 * 1024;
    public const int MaxValueChars = 200;

    private const string TypeField = "type";
    private const string SubtypeField = "subtype";
    private const string SessionIdField = "session_id";
    private const string IsErrorField = "is_error";

    private readonly HashSet<string> _rulePaths;
    private readonly string? _authSourcePath;
    private readonly JsonAllowlist _allowlist;

    public EventFilter(ClaudeRules rules)
    {
        _authSourcePath = rules.P5?.Field;
        _rulePaths = new HashSet<string>(RulePaths(rules), StringComparer.Ordinal);
        var keptPaths = new HashSet<string>(_rulePaths, StringComparer.Ordinal) { TypeField, SubtypeField, SessionIdField, IsErrorField };
        if (_authSourcePath is not null)
        {
            keptPaths.Add(_authSourcePath);
        }
        _allowlist = new JsonAllowlist(keptPaths, MaxValueChars);
    }

    public FilteredEvent? Accept(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) > MaxLineBytes)
        {
            return null;
        }

        var kept = _allowlist.Read(Encoding.UTF8.GetBytes(line));
        if (kept is null || !kept.TryGetValue(TypeField, out var type) || type.Kind != JsonTokenType.String)
        {
            return null;
        }

        return new FilteredEvent(
            Type: type.Text,
            Subtype: StringOrNull(kept, SubtypeField),
            SessionId: StringOrNull(kept, SessionIdField),
            IsError: kept.TryGetValue(IsErrorField, out var isError) && isError.Kind is JsonTokenType.True or JsonTokenType.False
                ? isError.Kind == JsonTokenType.True
                : null,
            AuthSource: _authSourcePath is null ? null : StringOrNull(kept, _authSourcePath),
            LimitFields: kept
                .Where(k => _rulePaths.Contains(k.Key))
                .ToDictionary(k => k.Key, k => k.Value.Literal, StringComparer.Ordinal));
    }

    private static string? StringOrNull(Dictionary<string, JsonScalar> kept, string path) =>
        kept.TryGetValue(path, out var value) && value.Kind == JsonTokenType.String ? value.Text : null;

    private static IEnumerable<string> RulePaths(ClaudeRules rules)
    {
        foreach (var requirement in rules.Result?.SuccessWhen ?? [])
        {
            yield return requirement.Path;
        }
        if (rules.Usage is { } usage)
        {
            yield return usage.ResetField;
            foreach (var field in usage.UtilizationFields)
            {
                yield return field;
            }
            yield return usage.Overage.StatusField;
            yield return usage.Overage.InUseField;
        }
    }
}
