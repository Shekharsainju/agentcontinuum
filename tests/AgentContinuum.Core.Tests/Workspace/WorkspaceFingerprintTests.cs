using AgentContinuum.Core.Workspace;

namespace AgentContinuum.Core.Tests.Workspace;

public sealed class WorkspaceFingerprintTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly GitRepo _repo;

    public WorkspaceFingerprintTests()
    {
        _repo = GitRepo.Init(_dir.File("repo"));
        _repo.Write("src/a.txt", "alpha");
        _repo.Write(".gitignore", "*.log\n");
        _repo.CommitAll();
    }

    public void Dispose() => _dir.Dispose();

    private string Hash(string? worktree = null)
    {
        var result = WorkspaceFingerprint.Compute(worktree ?? _repo.Path, GitRepo.GitExe);
        Assert.Null(result.Error);
        Assert.False(result.Incomplete);
        return Assert.IsType<string>(result.Hash);
    }

    [Fact]
    public void Fingerprint_StableAcrossRuns()
    {
        Assert.Equal(Hash(), Hash());
        Assert.Matches("^[0-9a-f]{64}$", Hash());
    }

    [Fact]
    public void Fingerprint_ChangesOnEdit()
    {
        var before = Hash();

        _repo.Write("src/a.txt", "alphb");

        Assert.NotEqual(before, Hash());
    }

    [Fact]
    public void Fingerprint_ChangesOnNewUntrackedFile()
    {
        var before = Hash();

        _repo.Write("new.txt", "");

        Assert.NotEqual(before, Hash());
    }

    [Fact]
    public void Fingerprint_ChangesOnCommit()
    {
        var before = Hash();

        _repo.CommitAll("empty");

        Assert.NotEqual(before, Hash());
    }

    [Fact]
    public void Fingerprint_IgnoresGitignored()
    {
        var before = Hash();

        _repo.Write("build.log", "noise");

        Assert.Equal(before, Hash());
    }
}
