namespace AgentContinuum.Cli;

public sealed record AppPaths(string Database, string LaunchRoot, string LogFile)
{
    public static AppPaths ForRoot(string root) =>
        new(Path.Combine(root, "state", "ac.db"), Path.Combine(root, "launch"), Path.Combine(root, "logs", "ac.log"));

    // The variable, not the shell folder: the shell answer ignores a changed LOCALAPPDATA, which the
    // acceptance tests use to keep each run's state apart from yours.
    public static AppPaths ForCurrentUser()
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrEmpty(localAppData) || !Path.IsPathFullyQualified(localAppData))
        {
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        return ForRoot(Path.Combine(localAppData, "AgentContinuum"));
    }
}
