using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenMono.Windows.Desktop.Services;
using OpenMono.Windows.Supervisor;

namespace OpenMono.Windows.Desktop.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        var s = App.State.Supervisor;
        EndpointBox.Text = s.RemoteEndpointOverride ?? string.Empty;
        DockerBox.IsChecked = App.State.DockerWanted;
        VisionBox.IsChecked = s.VisionEnabled;
        WorkspaceBox.Text = App.State.Workspace;
        ModelsBox.Text = s.ModelsDirectory;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = App.State.Supervisor;
        var endpoint = EndpointBox.Text.Trim();
        App.State.Supervisor = s with
        {
            RemoteEndpointOverride = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint,
            VisionEnabled = VisionBox.IsChecked == true,
            ModelsDirectory = string.IsNullOrWhiteSpace(ModelsBox.Text) ? s.ModelsDirectory : ModelsBox.Text,
        };
        App.State.DockerWanted = DockerBox.IsChecked == true;
        App.State.Workspace = string.IsNullOrWhiteSpace(WorkspaceBox.Text) ? App.State.Workspace : WorkspaceBox.Text;
        Note.Text = "Saved. Restart inference on the Server page to apply endpoint and vision changes.";
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        var version = ReadVersion();
        var info = await new UpdateService().CheckAsync(version);
        if (info is null)
        {
            Note.Text = $"Up to date ({version}).";
            return;
        }

        Note.Text = $"Update available: {info.Version}. Opening {info.DownloadUrl}.";
        await new UpdateService().ApplyUpdateAsync(info);
    }

    private static string ReadVersion()
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 6; i++)
            {
                var candidate = Path.Combine(dir, "VERSION.windows");
                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate).Trim();
                }

                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar)) ?? dir;
            }
        }
        catch
        {
        }

        return "1.0.0-preview.1";
    }
}
