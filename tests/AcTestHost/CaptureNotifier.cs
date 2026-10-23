using AgentContinuum.Core.Platform;
using AgentContinuum.Core.Schedule;
using AgentContinuum.Windows;

namespace AgentContinuum.AcTestHost;

/// <summary>Appends the text each notification shows to a file, one tab-separated line each, and can show the toast too.</summary>
public sealed class CaptureNotifier : INotifier
{
    private readonly string? _path;
    private readonly INotifier? _toasts;
    private readonly object _gate = new();

    public CaptureNotifier(string? path, INotifier? toasts)
    {
        _path = path;
        _toasts = toasts;
    }

    public void OfferResume(Offer offer)
    {
        Append("offer", offer.TaskId, ToastNotifier.OfferText(offer));
        _toasts?.OfferResume(offer);
    }

    public void Attention(string taskId, string label, string reasonCode)
    {
        Append("attention", taskId, ToastNotifier.AttentionText(label, reasonCode));
        _toasts?.Attention(taskId, label, reasonCode);
    }

    private void Append(string kind, string taskId, string[] text)
    {
        if (_path is null)
        {
            return;
        }
        lock (_gate)
        {
            File.AppendAllText(_path, string.Join('\t', [kind, taskId, .. text]) + "\n");
        }
    }
}
