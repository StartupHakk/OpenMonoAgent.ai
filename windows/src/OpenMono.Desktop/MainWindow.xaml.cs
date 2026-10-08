using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenMono.Windows.Desktop.Views;

namespace OpenMono.Windows.Desktop;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "OpenMono";
        // First run wizard shows when no model is installed; otherwise chat.
        bool firstRun = App.State.NeedsFirstRun();
        ContentFrame.Navigate(firstRun ? typeof(WizardPage) : typeof(ChatPage));
        Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(i => (string)i.Tag == (firstRun ? "wizard" : "chat"))
            ?? Nav.MenuItems.OfType<NavigationViewItem>().First();
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        ContentFrame.Navigate(tag switch
        {
            "models" => typeof(ModelsPage),
            "server" => typeof(ServerPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(ChatPage),
        });
    }
}
