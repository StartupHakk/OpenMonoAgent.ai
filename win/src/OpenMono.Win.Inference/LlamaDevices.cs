using System.Text.RegularExpressions;

namespace OpenMono.Win.Inference;

// Reads `llama-server --list-devices`. The Windows builds load ggml-cuda.dll
// and ggml-vulkan.dll at run time; when one cannot load (no cudart, an old
// driver) llama-server does not fail, it runs on the CPU. The device list is
// the only early sign of that.
public static partial class LlamaDevices
{
    public const string Arguments = "--list-devices";

    [GeneratedRegex(@"^\s+((?:CUDA|Vulkan|ROCm|SYCL|Metal)\d+):\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex DeviceLine();

    public static IReadOnlyList<string> Parse(string? output) =>
        string.IsNullOrEmpty(output)
            ? []
            : DeviceLine().Matches(output).Select(m => $"{m.Groups[1].Value}: {m.Groups[2].Value.Trim()}").ToList();

    public static string Prefix(Accel accel) => accel switch
    {
        Accel.Cuda => "CUDA",
        Accel.Vulkan => "Vulkan",
        _ => "",
    };

    // The first device this build would run the model on, or null when it has none.
    public static string? FirstFor(Accel accel, string? output) =>
        accel == Accel.Cpu
            ? null
            : Parse(output).FirstOrDefault(d => d.StartsWith(Prefix(accel), StringComparison.OrdinalIgnoreCase));
}
