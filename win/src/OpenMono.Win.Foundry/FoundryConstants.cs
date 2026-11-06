namespace OpenMono.Win.Foundry;

// Shared constants for the Windows Foundry host. Plain data only, no I/O,
// so every consumer (host, launcher, tests) agrees on one set of values.
public static class FoundryConstants
{
    // Loopback endpoint the Windows host binds or adopts. OMA config spells
    // the default as http://localhost:7474; same interface, either spelling works.
    public const string DefaultLoopbackEndpoint = "http://127.0.0.1:7474";

    public const int DefaultPort = 7474;

    // Top of FoundryModelLadder: the coding model from the Nov 2026 Foundry
    // Local announcement, and the only model a 24 GB+ card ever serves.
    // Resolved against the live catalog at runtime; this is only the alias.
    public const string TargetModelAlias = "qwen3.8-27b";

    // Minimum Foundry Local CLI with the Qwen3.8-27B coding stack.
    public static readonly Version MinCliVersion = new(0, 13, 0);

    // INT4 Qwen3.8-27B needs about 18 GB of weights; a 24 GB card is the
    // supported floor. Cards report a little under their marketed size
    // (RTX 4090: 24564 MiB, RTX 5090: 32607 MiB), so each class is matched
    // from 1 GiB below its nominal size.
    public const long RequiredVramBytes27B = 23L * 1024 * 1024 * 1024;

    // 32 GB class cards and above unlock the model's full context window.
    public const long FullContextVramBytes = 31L * 1024 * 1024 * 1024;

    // Context windows from the Foundry announcement: up to 64K on 24 GB
    // cards, up to the full 262K with headroom.
    public const int StandardContextTokens = 64 * 1024;

    public const int MaxContextTokens = 262144;

    // Assembly probed at runtime for the optional C# SDK path (M1: probe only).
    public const string SdkAssemblyName = "Microsoft.AI.Foundry.Local";

    public const string CliExecutableWindows = "foundry.exe";

    public const string CliExecutableUnix = "foundry";
}
