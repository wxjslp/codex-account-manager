using Microsoft.UI.Xaml;

namespace CodexAccountManager.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        UnhandledException += (_, args) =>
        {
            AppCrashLog.Write(args.Exception);
        };

        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            AppCrashLog.Write(ex);
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            AppCrashLog.Write(ex);
            throw;
        }
    }
}
