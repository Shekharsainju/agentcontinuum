using AgentContinuum.Core.Platform;
using AgentContinuum.Core.Schedule;
using Microsoft.Toolkit.Uwp.Notifications;

namespace AgentContinuum.Windows;

/// <summary>Windows notifications. Only the task label and fixed text are shown.</summary>
public sealed class ToastNotifier : INotifier
{
    public void OfferResume(Offer offer)
    {
        var title = offer switch
        {
            { LaunchDidNotStart: true } => $"{offer.Label}: the launch did not start",
            { ResetTimeUnknown: true } => $"{offer.Label}: usage limit hit, reset time unknown",
            _ => $"{offer.Label}: usage limit has reset",
        };
        new ToastContentBuilder()
            .AddText(title)
            .AddButton(new ToastButton("Resume", ResumeArguments(offer.TaskId)))
            .AddButton(new ToastButtonDismiss("Dismiss"))
            .Show(toast => toast.Tag = offer.TaskId);
    }

    // No Resume button: a task needing attention is resumed only with ac resume, after you have looked at it.
    public void Attention(string taskId, string label, string reasonCode)
    {
        new ToastContentBuilder()
            .AddText($"{label} needs attention")
            .AddText(reasonCode)
            .AddButton(new ToastButtonDismiss("Dismiss"))
            .Show(toast => toast.Tag = taskId);
    }

    public static string ResumeArguments(string taskId) => $"action=resume&task={taskId}";

    /// <summary>True when Windows started this process for a notification click.</summary>
    public static bool StartedByClick() => ToastNotificationManagerCompat.WasCurrentProcessToastActivated();

    /// <summary>Calls <paramref name="handler"/> with the clicked button's arguments.</summary>
    public static void OnClick(Action<string> handler) =>
        ToastNotificationManagerCompat.OnActivated += activation => handler(activation.Argument);
}
