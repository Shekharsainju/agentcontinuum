using System.Text;

namespace AgentContinuum.Core.State;

public static class StateText
{
    public static string ToText<T>(T value)
        where T : struct, Enum
    {
        var name = value.ToString();
        var text = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                text.Append('_');
            }
            text.Append(char.ToUpperInvariant(name[i]));
        }
        return text.ToString();
    }

    public static T Parse<T>(string text)
        where T : struct, Enum
    {
        foreach (var value in Enum.GetValues<T>())
        {
            if (ToText(value) == text)
            {
                return value;
            }
        }
        throw new FormatException($"Unknown {typeof(T).Name} '{text}'.");
    }
}
