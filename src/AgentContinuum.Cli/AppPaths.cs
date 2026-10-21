namespace AgentContinuum.Cli;

public sealed record AppPaths(string Database, string LaunchRoot, string LogFile)
{
    public static AppPaths ForRoot(string root) =>
        new(Path.Combine(root, "state", "ac.db"), Path.Combine(root, "launch"), Path.Combine(root, "logs", "ac.log"));

    public static AppPaths ForCurrentUser() =>
        ForRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentContinuum"));
}
