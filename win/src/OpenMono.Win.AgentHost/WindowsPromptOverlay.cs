namespace OpenMono.Win.AgentHost;

// System prompt overlay for Windows sessions. Upstream prompts assume Unix;
// this overlay is appended by the host so the model reasons about Windows
// paths, PowerShell, and the Foundry endpoint.
public static class WindowsPromptOverlay
{
    public const string OverlayText =
        "You are running on Windows. Use PowerShell syntax (pwsh) for shell commands, "
        + "with `;` or newlines to chain, never `&&` bash-isms like `head`, `grep`, or `sed` "
        + "(prefer reading files with your file tools). Paths look like C:\\Users\\... and file URIs "
        + "look like file:///C:/.... Inference is served locally by Microsoft Foundry Local on "
        + "http://127.0.0.1:7474; if the model endpoint is unreachable, tell the user to run "
        + "`foundry server status` and check that CUDA/cuDNN DLLs are on PATH, instead of "
        + "suggesting Docker or llama-server commands. Never run destructive Windows commands "
        + "(format, diskpart, bcdedit, reg delete, recursive deletes of C:\\ or C:\\Windows).";
}
