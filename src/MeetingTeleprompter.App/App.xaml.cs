using Microsoft.UI.Xaml;

namespace MeetingTeleprompter.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        var command = Environment.GetCommandLineArgs();
        // Explicit test-only startup diagnostics, never routine transcript logging.
        if (command.Length == 4 && command[1] == "--ui-validation")
            UnhandledException += (_, e) => File.WriteAllText(command[3], e.Exception.ToString());
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var command = Environment.GetCommandLineArgs();
        if (command.Length == 5 && command[1] == "--validate-audio")
        {
            await AudioValidation.RunAsync(command[2], command[3], command[4]);
            Exit();
            return;
        }
        _window = new MainWindow(command.Length == 4 && command[1] == "--ui-validation" ? command[2] : null);
        _window.Activate();
    }
}
