using System.Text;

namespace AgentContinuum.Windows;

/// <summary>Builds a command line that CommandLineToArgvW and the C runtime split back into exactly the given args.</summary>
internal static class CommandLine
{
    public static string Build(string exe, IEnumerable<string> args)
    {
        // argv[0] is split on quotes only, with no backslash escapes, so a quote inside the path cannot be expressed.
        if (exe.Contains('"') || exe.Contains('\0'))
        {
            throw new ArgumentException("The executable path cannot contain quotes.", nameof(exe));
        }
        var line = new StringBuilder().Append('"').Append(exe).Append('"');
        foreach (var arg in args)
        {
            if (arg.Contains('\0'))
            {
                throw new ArgumentException("An argument cannot contain a NUL character.", nameof(args));
            }
            line.Append(' ').Append(Quote(arg));
        }
        return line.ToString();
    }

    private static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return arg;
        }
        var quoted = new StringBuilder().Append('"');
        for (var i = 0; i < arg.Length; i++)
        {
            var backslashes = 0;
            while (i < arg.Length && arg[i] == '\\')
            {
                backslashes++;
                i++;
            }
            if (i == arg.Length)
            {
                quoted.Append('\\', backslashes * 2);
                break;
            }
            if (arg[i] == '"')
            {
                quoted.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                quoted.Append('\\', backslashes).Append(arg[i]);
            }
        }
        return quoted.Append('"').ToString();
    }
}
