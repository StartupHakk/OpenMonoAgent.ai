using Microsoft.UI.Xaml;
using OpenMono.Windows.Desktop.Services;

namespace OpenMono.Windows.Desktop;

public partial class App : Application
{
    public static AppState State { get; } = new();

    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
