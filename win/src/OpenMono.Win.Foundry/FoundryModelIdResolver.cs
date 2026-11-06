namespace OpenMono.Win.Foundry;

// Maps a catalog alias onto the model id the running server actually serves.
// Foundry's /v1/models lists full variant ids (for example
// "qwen3.8-27b-cuda-gpu:1", "qwen3.5-9b-cuda-gpu:4"), not the alias that was loaded, and
// chat completions want the served id. The text after the alias must be a
// variant suffix (instruct, generic, cuda, gpu, ...) or a ":version" tag, so
// a short alias never captures a different model ("phi-4" must not match
// "phi-4-mini-...", "qwen3" must not match "qwen3.8-27b").
public static class FoundryModelIdResolver
{
    private static readonly string[] VariantTokens =
    [
        "instruct", "chat", "it", "generic", "cuda", "gpu", "cpu", "npu",
        "openvino", "trtrtx", "tensorrt", "webgpu", "dml", "qnn", "vitis", "int4", "fp16", "onnx",
    ];

    public static string? ResolveServedId(string alias, IEnumerable<string> servedIds)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return null;
        }

        var ids = servedIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToList();

        var exact = ids.FirstOrDefault(id => string.Equals(id, alias, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var wanted = Canonical(alias);
        var candidates = ids.Where(id => IsVariantOf(Canonical(id), wanted)).ToList();

        // Prefer the GPU variant when several variants of one alias are served.
        return candidates.FirstOrDefault(id => id.Contains("cuda", StringComparison.OrdinalIgnoreCase)
                || id.Contains("gpu", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault();
    }

    private static bool IsVariantOf(string id, string alias)
    {
        if (!id.StartsWith(alias, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = id[alias.Length..];
        if (rest.Length == 0 || rest[0] == ':')
        {
            return true;
        }

        if (rest[0] != '-')
        {
            return false;
        }

        var next = rest[1..].Split('-', ':')[0];
        return VariantTokens.Contains(next, StringComparer.Ordinal);
    }

    // Lowercase and fold '_' and ' ' to '-' so "Qwen3.8_27B" and "qwen3.8-27b"
    // compare equal.
    private static string Canonical(string value) =>
        value.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
}
