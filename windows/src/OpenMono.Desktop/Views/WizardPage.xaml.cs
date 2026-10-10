using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenMono.Windows.Hardware;
using OpenMono.Windows.Models;
using OpenMono.Windows.Supervisor;

namespace OpenMono.Windows.Desktop.Views;

public sealed partial class WizardPage : Page
{
    /// <summary>
    /// Set by SkipDocker_Click. The Ready step (M1.7) reads it to skip the
    /// Docker stack start and show the DuckDuckGo fallback note instead.
    /// </summary>
    public bool DockerSkipped { get; private set; }

    public WizardPage()
    {
        InitializeComponent();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        await Task.Run(() => App.State.RefreshHardware());
        var state = App.State;
        var registry = state.EnsureRegistry();
        var hw = state.Hardware;
        if (hw is null)
        {
            HardwareText.Text = "Hardware detection is unavailable. CPU model will be used.";
            state.SelectedTier = registry.ForTier(0);
            ModelText.Text = state.SelectedTier.Label;
            return;
        }

        var gpuLine = hw.Gpus.Count > 0
            ? string.Join("; ", hw.Gpus.Select(g => $"{g.Name} ({g.DedicatedBytes / (1024 * 1024 * 1024)}GB)"))
            : "no GPU detected";
        HardwareText.Text = $"GPU: {gpuLine}. RAM: {hw.Memory.TotalRamBytes / (1024 * 1024 * 1024)}GB. Cores: {hw.Memory.PhysicalCores}. {string.Join(" ", hw.Selection.Warnings)}";
        state.SelectedTier = registry.ForTier((int)hw.Selection.Tier);
        ModelText.Text = $"{state.SelectedTier.Label} (flavor: {hw.Selection.Flavor}, context: {state.SelectedTier.EffectiveCtx(state.Supervisor.VisionEnabled)})";
        var docker = await DockerDetector.DetectAsync();
        DockerText.Text = docker.EngineAvailable
            ? $"Docker Desktop found (server {docker.ServerVersion ?? "unknown"}, compose: {(docker.ComposeAvailable ? "yes" : "no")}). Caddy, SearXNG, and Scrapling will start after inference is healthy."
            : $"Docker Desktop not found: {docker.Detail} {DockerDetector.SkippedExplanation()}";
    }

    private void SkipDocker_Click(object sender, RoutedEventArgs e)
    {
        DockerSkipped = true;
        App.State.DockerWanted = false;
        DockerText.Text = DockerDetector.SkippedExplanation();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var state = App.State;
        try
        {
            var tier = state.SelectedTier ?? state.EnsureRegistry().ForTier(0);
            var downloader = new ModelDownloader();
            var progress = new Progress<ModelDownloader.DownloadProgress>(p =>
                DispatcherQueue.TryEnqueue(() =>
                {
                    Progress.Value = p.Percent ?? 0;
                    ProgressText.Text = $"{p.FileName}: {p.ReceivedBytes / (1024 * 1024)}MB {p.Status}";
                }));
            var mirror = Environment.GetEnvironmentVariable("OPENMONO_MODEL_MIRROR");
            await downloader.DownloadAsync(
                ModelRegistry.ApplyMirror(tier.ModelUrl, mirror),
                Path.Combine(state.Supervisor.ModelsDirectory, tier.ModelName),
                tier.Sha256,
                progress);
            if (state.Supervisor.VisionEnabled && !string.IsNullOrWhiteSpace(tier.Mmproj))
            {
                await downloader.DownloadAsync(
                    ModelRegistry.ApplyMirror(tier.MmprojUrl, mirror),
                    Path.Combine(state.Supervisor.ModelsDirectory, tier.Mmproj),
                    tier.MmprojSha256,
                    progress);
            }

            ProgressText.Text = "Model ready. Start inference on the Server page, then open Chat.";
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
            else
            {
                Frame.Navigate(typeof(ChatPage));
            }
        }
        catch (Exception ex)
        {
            ProgressText.Text = $"Setup failed: {ex.Message}";
        }
    }
}
