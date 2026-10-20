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
    public void Fingerprint_SpacesAndNonAscii_Stable()
    {
        var repo = GitRepo.Init(_dir.File(Path.Combine("a b", "rèpo ñ")));
        repo.Write("ü ñ.txt", "first");
        repo.CommitAll();
        repo.Write("dir ä/新しい.txt", "untracked");

        var before = Hash(repo.Path);
        Assert.Equal(before, Hash(repo.Path));

        repo.Write("dir ä/新しい.txt", "edited");

        Assert.NotEqual(before, Hash(repo.Path));
    }

    [Fact]
    public void Fingerprint_RepoFsmonitorHookNotRun()
    {
        var marker = _dir.File("fsmonitor-ran");
        var hook = _dir.File("fsmonitor.sh");
        File.WriteAllText(hook, $"#!/bin/sh\necho ran > '{marker.Replace('\\', '/')}'\nexit 1\n");
        _repo.Git("config", "core.fsmonitor", hook.Replace('\\', '/'));
        _repo.Git("ls-files");
        Assert.True(File.Exists(marker), "plain ls-files should run the repo's fsmonitor hook");
        File.Delete(marker);

        Hash();

        Assert.False(File.Exists(marker));
    }

    [Theory]
    [InlineData(2, 11, false)]
    [InlineData(1, 11, true)]
    [InlineData(2, 10, true)]
    public void Fingerprint_OverLimit_Incomplete(int maxFiles, long maxBytes, bool incomplete)
    {
        var result = WorkspaceFingerprint.Compute(_repo.Path, GitRepo.GitExe, maxFiles, maxBytes);

        Assert.Equal(incomplete, result.Incomplete);
        Assert.Equal(incomplete, result.Hash is null);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Fingerprint_DefaultLimits_50000FilesAnd2GB()
    {
        Assert.Equal(50_000, WorkspaceFingerprint.MaxFiles);
        Assert.Equal(2L * 1024 * 1024 * 1024, WorkspaceFingerprint.MaxBytes);
    }

    [Fact]
    public void Fingerprint_IgnoresGitignored()
    {
        var before = Hash();

        _repo.Write("build.log", "noise");

        Assert.Equal(before, Hash());
    }
}
