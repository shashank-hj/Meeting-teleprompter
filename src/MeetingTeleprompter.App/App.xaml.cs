using Microsoft.UI.Xaml;

namespace MeetingTeleprompter.App;

public partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var command = Environment.GetCommandLineArgs();
        if (command.Length == 5 && command[1] == "--validate-audio")
        {
            await AudioValidation.RunAsync(command[2], command[3], command[4]);
            Exit();
            return;
        }
        _window = new MainWindow();
        _window.Activate();
    }
}
