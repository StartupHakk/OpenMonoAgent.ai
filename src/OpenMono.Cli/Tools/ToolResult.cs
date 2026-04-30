namespace OpenMono.Tools;

public sealed record ToolResult
{

    public required string ModelPreview { get; init; }

    public object? MachinePayload { get; init; }

    public IReadOnlyList<ArtifactRef> Artifacts { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public IReadOnlyList<SideEffect> SideEffects { get; init; } = [];

    public ResultClass Class { get; init; } = ResultClass.Success;

    public string? RetryHint { get; init; }

    public string? CacheKey { get; init; }

    public string Content => ModelPreview;

    public bool IsError => Class != ResultClass.Success;

    public string? ErrorMessage => IsError ? ModelPreview : null;

    public Dictionary<string, object>? Metadata { get; init; }

    public static ToolResult Success(string content, Dictionary<string, object>? metadata = null) =>
        new() { ModelPreview = content, Metadata = metadata, Class = ResultClass.Success };

    public static ToolResult Error(string message) =>
        new() { ModelPreview = message, Class = ResultClass.InvalidInput };

    public static ToolResult SuccessWithPayload(string preview, object payload) =>
        new() { ModelPreview = preview, MachinePayload = payload, Class = ResultClass.Success };

    public static ToolResult InvalidInput(string preview, string retryHint) =>
        new() { ModelPreview = preview, Class = ResultClass.InvalidInput, RetryHint = retryHint };

    public static ToolResult PermissionDenied(string preview, string? retryHint = null) =>
        new() { ModelPreview = preview, Class = ResultClass.PermissionDenied, RetryHint = retryHint };

    public static ToolResult StateConflict(string preview, string retryHint) =>
        new() { ModelPreview = preview, Class = ResultClass.StateConflict, RetryHint = retryHint };

    public static ToolResult Crash(string preview, string retryHint) =>
        new() { ModelPreview = preview, Class = ResultClass.Crash, RetryHint = retryHint };

    public static ToolResult Empty(string preview) =>
        new() { ModelPreview = preview, Class = ResultClass.Empty };

    public static ToolResult Cancelled(string preview = "Operation was cancelled") =>
        new() { ModelPreview = preview, Class = ResultClass.Cancelled };

    public ToolResult WithWarnings(params string[] warnings) =>
        this with { Warnings = [.. Warnings, .. warnings] };

    public ToolResult WithSideEffects(params SideEffect[] effects) =>
        this with { SideEffects = [.. SideEffects, .. effects] };

    public ToolResult WithArtifacts(params ArtifactRef[] artifacts) =>
        this with { Artifacts = [.. Artifacts, .. artifacts] };

    public ToolResult WithCacheKey(string cacheKey) =>
        this with { CacheKey = cacheKey };

    public string? Diff { get; init; }

    public ToolResult WithDiff(string? diff) =>
        diff is null ? this : this with { Diff = diff };
}

public enum ResultClass
{

    Success,

    InvalidInput,

    PermissionDenied,

    StateConflict,

    Crash,

    Empty,

    Cancelled
}

public sealed record ArtifactRef(string Id, string Kind, long Bytes, string Path);

public sealed record SideEffect(string Kind, string Target, IReadOnlyDictionary<string, string> Meta)
{

    public static SideEffect FileWrite(string path, long bytes) =>
        new("file_write", path, new Dictionary<string, string> { ["bytes"] = bytes.ToString() });

    public static SideEffect FileDelete(string path) =>
        new("file_delete", path, new Dictionary<string, string>());

    public static SideEffect ProcessSpawn(string command, int? pid = null) =>
        new("process_spawn", command, pid.HasValue
            ? new Dictionary<string, string> { ["pid"] = pid.Value.ToString() }
            : new Dictionary<string, string>());
}
