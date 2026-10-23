using AgentContinuum.Cli.Commands;
using AgentContinuum.Windows;

namespace AgentContinuum.Cli;

/// <summary>Everything ac.exe does from its arguments, so another host can run the same code with its own services.</summary>
public static class Entry
{
    /// <param name="services">Built inside the hook's guard, so a failing set-up cannot escape the hook.</param>
    /// <param name="notificationClicks">False for a host that never shows real notifications.</param>
    public static async Task<int> Run(string[] args, Func<AppServices> services, bool notificationClicks = true)
    {
        // Claude Code runs the hook, and a hook that fails or prints can change how the run ends, so nothing in
        // set-up may escape it either.
        if (args is ["hook", ..])
        {
            try
            {
                return await App.Run(args, services());
            }
            catch (Exception)
            {
                return 0;
            }
        }

        var app = services();
        var startedByClick = notificationClicks && ToastNotifier.StartedByClick();
        if (notificationClicks && (startedByClick || args is ["daemon"]))
        {
            var clicked = new TaskCompletionSource();
            ToastNotifier.OnClick(arguments =>
            {
                ToastActivation.Handle(arguments, app);
                clicked.TrySetResult();
            });
            if (startedByClick)
            {
                // Windows delivers the click to the handler after start-up; the process exits once it is sent.
                await Task.WhenAny(clicked.Task, Task.Delay(TimeSpan.FromSeconds(10)));
                return 0;
            }
        }

        return await App.Run(args, app);
    }
}
