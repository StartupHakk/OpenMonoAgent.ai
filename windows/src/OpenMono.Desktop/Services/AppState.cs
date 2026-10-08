using OpenMono.Windows.Hardware;
using OpenMono.Windows.Models;
using OpenMono.Windows.Supervisor;

namespace OpenMono.Windows.Desktop.Services;

/// <summary>
/// Shared app state: supervisor config, hardware report, llama supervisor,
/// Docker stack manager, and the in-process agent session handle.
/// </summary>
public sealed class AppState
{
    public SupervisorConfig Supervisor { get; set; } = new();
    public HardwareReport? Hardware { get; set; }
    public ModelRegistry? Registry { get; set; }
    public ModelTier? SelectedTier { get; set; }
    public LlamaServerSupervisor? Llama { get; set; }
    public string Workspace { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "code");

    public bool DockerWanted { get; set; } = true;

    public void RefreshHardware()
    {
        Hardware = HardwareReport.Collect();
    }

    public ModelRegistry EnsureRegistry()
{
        Registry ??= ModelRegistry.Load();
        return Registry;
    }

    public bool NeedsFirstRun()
    {
        try
        {
            var registry = EnsureRegistry();
            var tier = SelectedTier ?? registry.ForTier(0);
            return !File.Exists(Path.Combine(Supervisor.ModelsDirectory, tier.ModelName));
        }
        catch
        {
            return true;
        }
    }

    public DockerComposeManager ComposeManager(string repoRoot) =>
        new(repoRoot, Supervisor);
}
