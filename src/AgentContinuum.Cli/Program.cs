using AgentContinuum.Cli;
using AgentContinuum.Cli.Commands;
using AgentContinuum.Windows;

// Claude Code runs the hook, and a hook that fails or prints can change how the run ends, so nothing in
// set-up may escape it either.
if (args is ["hook", ..])
{
    try
    {
        return await App.Run(args, AppServices.ForCurrentUser());
    }
    catch (Exception)
    {
        return 0;
    }
}

var services = AppServices.ForCurrentUser();
var startedByClick = ToastNotifier.StartedByClick();
if (startedByClick || args is ["daemon"])
{
    var clicked = new TaskCompletionSource();
    ToastNotifier.OnClick(arguments =>
    {
        ToastActivation.Handle(arguments, services);
        clicked.TrySetResult();
    });
    if (startedByClick)
    {
        // Windows delivers the click to the handler after start-up; the process exits once it is sent.
        await Task.WhenAny(clicked.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        return 0;
    }
}

return await App.Run(args, services);
