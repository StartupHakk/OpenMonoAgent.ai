using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenMono.Windows.Hardware;
using OpenMono.Windows.Models;

namespace OpenMono.Windows.Desktop.Views;

public sealed partial class ModelsPage : Page
{
    private CancellationTokenSource? _cts;

    public ModelsPage()
    {
        InitializeComponent();
        var state = App.State;
        state.RefreshHardware();
        var registry = state.EnsureRegistry();
        Recommendation.Text = state.Hardware is { } hw
            ? $"Detected: {(hw.Gpus.Count > 0 ? hw.Gpus[0].Name : "no GPU")} - recommended tier {(int)hw.Selection.Tier}. {string.Join(" ", hw.Selection.Warnings)}"
            : "Hardware detection is unavailable.";
        TierList.ItemsSource = registry.Tiers.Select(t => $"{t.Tier}: {t.Label}").ToList();
    }

    private void TierList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var registry = App.State.EnsureRegistry();
        var index = TierList.SelectedIndex;
        if (index >= 0)
        {
            App.State.SelectedTier = registry.Tiers[index];
        }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        var state = App.State;
        var tier = state.SelectedTier ?? state.EnsureRegistry().ForTier(0);
        _cts = new CancellationTokenSource();
        var downloader = new ModelDownloader();
        var progress = new Progress<ModelDownloader.DownloadProgress>(p =>
            DispatcherQueue.TryEnqueue(() =>
            {
                Progress.Value = p.Percent ?? 0;
                ProgressText.Text = $"{p.FileName}: {p.ReceivedBytes / (1024 * 1024)}MB {p.Status}";
            }));
        try
        {
            var mirror = Environment.GetEnvironmentVariable("OPENMONO_MODEL_MIRROR");
            var modelUrl = ModelRegistry.ApplyMirror(tier.ModelUrl, mirror);
            var mmprojUrl = ModelRegistry.ApplyMirror(tier.MmprojUrl, mirror);
            await downloader.DownloadAsync(modelUrl, Path.Combine(state.Supervisor.ModelsDirectory, tier.ModelName), tier.Sha256, progress, ct: _cts.Token);
            if (!string.IsNullOrWhiteSpace(tier.Mmproj) && state.Supervisor.VisionEnabled)
            {
                await downloader.DownloadAsync(mmprojUrl, Path.Combine(state.Supervisor.ModelsDirectory, tier.Mmproj), tier.MmprojSha256, progress, ct: _cts.Token);
            }

            ProgressText.Text = $"Installed {tier.ModelName}.";
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "Download cancelled.";
        }
        catch (Exception ex)
        {
            ProgressText.Text = $"Download failed: {ex.Message}";
        }
    }

    private void Custom_Click(object sender, RoutedEventArgs e)
    {
        ProgressText.Text = "Copy any .gguf into the models folder and restart inference. If the family changes, update or disable the mmproj vision file.";
    }
}
