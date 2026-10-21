namespace AgentContinuum.Cli;

public sealed record AppPaths(string Root)
{
    public string Database => Path.Combine(Root, "state", "ac.db");

    public string LaunchRoot => Path.Combine(Root, "launch");

    public string LogFile => Path.Combine(Root, "logs", "ac.log");

    public static AppPaths ForCurrentUser() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentContinuum"));
}
