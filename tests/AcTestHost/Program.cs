using AgentContinuum.AcTestHost;
using AgentContinuum.Cli;

if (GitShim.Matches(args))
{
    return GitShim.Run(args);
}

// The hook and a notification click start this process without the test's environment, so the config is
// looked up rather than passed in, and without one nothing may run against whatever state happens to be there.
var isHook = args is ["hook", ..];
if (TestHostConfig.Find(out var problem) is not { } config)
{
    if (!isHook)
    {
        Console.Error.WriteLine($"ac-test: {problem}");
    }
    return isHook ? 0 : TestHostConfig.NoConfigExitCode;
}

return await Entry.Run(args, () => TestServices.Build(config, args), config.RealToasts);
