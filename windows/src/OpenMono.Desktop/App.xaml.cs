using Microsoft.UI.Xaml;
using OpenMono.Windows.Desktop.Services;
using OpenMono.Windows.Supervisor;

namespace OpenMono.Windows.Desktop;

public partial class App : Application
{
    public static AppState State { get; } = new();

    private Window? _window;

    public App()
    {
        // Restore persisted supervisor fields (ports, folders, LAN serving,
        // API key). Missing or malformed app.json yields defaults.
        State.Supervisor = SupervisorStore.Load();
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
