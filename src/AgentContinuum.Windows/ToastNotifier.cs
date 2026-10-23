using AgentContinuum.Core.Platform;
using AgentContinuum.Core.Schedule;
using Microsoft.Toolkit.Uwp.Notifications;

namespace AgentContinuum.Windows;

/// <summary>Windows notifications. Only the task label and fixed text are shown.</summary>
public sealed class ToastNotifier : INotifier
{
    public void OfferResume(Offer offer)
    {
        AddLines(new ToastContentBuilder(), OfferText(offer))
            .AddButton(new ToastButton("Resume", ResumeArguments(offer.TaskId)))
            .AddButton(new ToastButtonDismiss("Dismiss"))
            .Show(toast => toast.Tag = offer.TaskId);
    }

    // No Resume button: a task needing attention is resumed only with ac resume, after you have looked at it.
    public void Attention(string taskId, string label, string reasonCode)
    {
        AddLines(new ToastContentBuilder(), AttentionText(label, reasonCode))
            .AddButton(new ToastButtonDismiss("Dismiss"))
            .Show(toast => toast.Tag = taskId);
    }

    /// <summary>The text lines of a resume offer, as the toast shows them.</summary>
    public static string[] OfferText(Offer offer) =>
    [
        offer switch
        {
            { LaunchDidNotStart: true } => $"{offer.Label}: the launch did not start",
            { ResetTimeUnknown: true } => $"{offer.Label}: usage limit hit, reset time unknown",
            _ => $"{offer.Label}: usage limit has reset",
        },
    ];

    /// <summary>The text lines of an attention notice, as the toast shows them.</summary>
    public static string[] AttentionText(string label, string reasonCode) => [$"{label} needs attention", reasonCode];

    public static string ResumeArguments(string taskId) => $"action=resume&task={taskId}";

    /// <summary>True when Windows started this process for a notification click.</summary>
    public static bool StartedByClick() => ToastNotificationManagerCompat.WasCurrentProcessToastActivated();

    /// <summary>Calls <paramref name="handler"/> with the clicked button's arguments.</summary>
    public static void OnClick(Action<string> handler) =>
        ToastNotificationManagerCompat.OnActivated += activation => handler(activation.Argument);

    private static ToastContentBuilder AddLines(ToastContentBuilder builder, IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            builder.AddText(line);
        }
        return builder;
    }
}
