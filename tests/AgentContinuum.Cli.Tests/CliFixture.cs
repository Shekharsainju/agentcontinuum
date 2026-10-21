using AgentContinuum.Core.Platform;
using AgentContinuum.Core.Preflight;
using AgentContinuum.Core.Rules;
using AgentContinuum.Core.Schedule;
using AgentContinuum.Core.Tests;
using AgentContinuum.Core.Tests.Launch;
using AgentContinuum.Core.Tests.Preflight;

namespace AgentContinuum.Cli.Tests;

/// <summary>Services around a task that passes pre-flight, with a fake process host and recorded output.</summary>
public sealed class CliFixture : IDisposable
{
    public const string TaskId = PreflightFixture.TaskId;

    public PreflightFixture F { get; } = new();

    public FakeProcessHost Host { get; } = new();

    public RecordingNotifier Notifier { get; } = new();

    public RecordingLog Log { get; } = new();

    public StringWriter Out { get; } = new();

    public MemoryStream In { get; set; } = new();

    public string LockName { get; } = $@"Local\AgentContinuum.Test.{Guid.NewGuid():N}";

    public AppServices Services()
    {
        F.EnsureAdded();
        var preflightHost = new PreflightHost(GitRepo.GitExe, F.ParentEnv, F.UserProfile, F.ManagedSettingsPaths);
        return new AppServices(
            new AppPaths(F.Store.Connection.DataSource, F.Outside("launch"), F.Outside("ac.log")),
            F.Clock,
            Log,
            Notifier,
            Host,
            F.Lister,
            F.Runner,
            preflightHost,
            @"C:\Program Files\AgentContinuum\ac.exe",
            Out,
            In,
            LockName,
            ClientWait: TimeSpan.FromSeconds(1),
            ReplyWait: TimeSpan.FromSeconds(10),
            RequestPoll: TimeSpan.FromMilliseconds(10),
            TickInterval: TimeSpan.FromMilliseconds(10),
            _ => VersionRules.For("2.1.304"),
            async _ => await Task.Yield());
    }

    public string Output => Out.ToString();

    public string Session => F.Task.SessionId.ToString("D");

    public string InitLine() =>
        $$"""{"type":"system","subtype":"init","session_id":"{{Session}}","apiKeySource":"none"}""";

    public string SuccessLine() =>
        $$"""{"type":"result","subtype":"success","session_id":"{{Session}}","is_error":false,"terminal_reason":"completed"}""";

    public void Dispose() => F.Dispose();

    public sealed class RecordingNotifier : INotifier
    {
        public List<Offer> Offers { get; } = [];

        public List<(string TaskId, string Label, string ReasonCode)> Attentions { get; } = [];

        public void OfferResume(Offer offer)
        {
            lock (Offers)
            {
                Offers.Add(offer);
            }
        }

        public void Attention(string taskId, string label, string reasonCode)
        {
            lock (Attentions)
            {
                Attentions.Add((taskId, label, reasonCode));
            }
        }
    }
}
