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
        if (command.Length == 4 && command[1] == "--render-ui")
        {
            Directory.CreateDirectory(command[3]);
            try
            {
                var preview = new MainWindow(command[2]);
                _window = preview; preview.Activate();
                await preview.RenderUiPreviewAsync(command[3]);
                preview.Close();
            }
            catch (Exception error) { await File.WriteAllTextAsync(Path.Combine(command[3], "render-error.txt"), error.ToString()); }
            Exit(); return;
        }
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
