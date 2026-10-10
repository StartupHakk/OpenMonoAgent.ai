using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenMono.Windows.Desktop.Services;
using OpenMono.Windows.Hardware;
using OpenMono.Windows.Supervisor;

namespace OpenMono.Windows.Desktop.Views;

public sealed partial class ServerPage : Page
{
    public ServerPage()
    {
        InitializeComponent();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var state = App.State;
        StatusLine.Text = state.Llama is { IsRunning: true }
            ? $"Running at {state.Supervisor.LlamaEndpoint}."
            : $"Stopped. Endpoint will be {state.Supervisor.LlamaEndpoint}.";
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var state = App.State;
        try
        {
            state.RefreshHardware();
            var registry = state.EnsureRegistry();
            var selection = state.Hardware?.Selection ?? ModelTierSelector.Select([], 0, false);
            var tier = state.SelectedTier ?? registry.ForTier((int)selection.Tier);
            int threads = state.Hardware is { } hw
                ? MemoryDetector.RecommendThreads(hw.Memory.PhysicalCores, hw.Memory.LogicalCores)
                : Environment.ProcessorCount;
            var spec = state.Supervisor.BuildLlamaCommand(tier, threads, selection.Flavor);
            state.Supervisor.LlamaPort = PortAllocator.Allocate(state.Supervisor.LlamaPort);
            SupervisorStore.Save(state.Supervisor);
            state.Llama = new LlamaServerSupervisor(state.Supervisor);
            StatusLine.Text = "Starting inference, waiting for health (up to 180s).";
            var progress = new Progress<string>(m => DispatcherQueue.TryEnqueue(() => StatusLine.Text = m));
            var result = await state.Llama.StartAsync(spec, progress);
            StatusLine.Text = result.Healthy
                ? $"Healthy at {state.Supervisor.LlamaEndpoint} (model: {result.Model ?? tier.Alias})."
                : $"Not healthy after {result.Attempts} checks. See logs.";
            await AgentHostFactory_WriteSettings(state, tier.Alias, tier.EffectiveCtx(state.Supervisor.VisionEnabled));
            LoadLogs();
        }
        catch (Exception ex)
        {
            StatusLine.Text = $"Start failed: {ex.Message}";
        }
    }

    private static Task AgentHostFactory_WriteSettings(AppState state, string alias, int ctx)
    {
        AgentHost.AgentHostFactory.WriteSettings(
            state.Supervisor.DataDirectory,
            state.Supervisor.LlamaEndpoint,
            alias,
            ctx,
            state.Supervisor.VisionEnabled,
            state.Supervisor.AcpPort,
            acpEnabled: false);
        return Task.CompletedTask;
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (App.State.Llama is { } llama)
        {
            await llama.StopAsync();
            App.State.Llama = null;
        }

        RefreshStatus();
    }

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        Stop_Click(sender, e);
        Start_Click(sender, e);
        await Task.CompletedTask;
    }

    private async void Diag_Click(object sender, RoutedEventArgs e)
    {
        var path = await DiagnosticsService.WriteBundleAsync(App.State);
        StatusLine.Text = $"Diagnostics written to {path}.";
    }

    private void LoadLogs()
    {
        try
        {
            var log = App.State.Llama?.LogPath;
            if (log is not null && File.Exists(log))
            {
                var lines = File.ReadAllLines(log);
                Logs.Text = string.Join('\n', lines.TakeLast(120));
            }
        }
        catch
        {
        }
    }
}
