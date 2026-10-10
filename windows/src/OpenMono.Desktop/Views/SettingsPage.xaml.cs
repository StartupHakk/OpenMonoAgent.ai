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
        LanBox.IsOn = s.AllowLanConnections;
        ApiKeyBox.Password = s.ApiKey ?? string.Empty;
        RefreshLanUrls();
    }

    private void RefreshLanUrls()
    {
        var s = App.State.Supervisor;
        if (!s.AllowLanConnections)
        {
            LanUrlText.Text = "LAN serving is off. Inference listens on localhost only.";
            return;
        }

        var urls = s.LanAdvertisedUrls();
        LanUrlText.Text = urls.Count > 0
            ? $"LAN clients connect to: {string.Join(", ", urls)}"
            : "LAN serving is on but no LAN IPv4 address was found. Check the network connection.";
    }

    private void GenerateKey_Click(object sender, RoutedEventArgs e)
    {
        ApiKeyBox.Password = SupervisorConfig.GenerateApiKey();
        Note.Text = "New API key generated. Click Save to apply it.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = App.State.Supervisor;
        var endpoint = EndpointBox.Text.Trim();
        var key = ApiKeyBox.Password.Trim();
        var lan = LanBox.IsOn;
        if (lan && string.IsNullOrWhiteSpace(key))
        {
            key = SupervisorConfig.GenerateApiKey();
            ApiKeyBox.Password = key;
        }

        var updated = s with
        {
            RemoteEndpointOverride = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint,
            VisionEnabled = VisionBox.IsChecked == true,
            ModelsDirectory = string.IsNullOrWhiteSpace(ModelsBox.Text) ? s.ModelsDirectory : ModelsBox.Text,
            AllowLanConnections = lan,
            ApiKey = string.IsNullOrWhiteSpace(key) ? null : key,
        };
        try
        {
            updated.ValidateLan();
        }
        catch (Exception ex)
        {
            Note.Text = ex.Message;
            return;
        }

        App.State.Supervisor = updated;
        SupervisorStore.Save(updated);
        App.State.DockerWanted = DockerBox.IsChecked == true;
        App.State.Workspace = string.IsNullOrWhiteSpace(WorkspaceBox.Text) ? App.State.Workspace : WorkspaceBox.Text;
        RefreshLanUrls();
        Note.Text = lan
            ? $"Saved. Restart inference on the Server page to apply. LAN clients: {string.Join(", ", updated.LanAdvertisedUrls())}."
            : "Saved. Restart inference on the Server page to apply endpoint and vision changes.";
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
