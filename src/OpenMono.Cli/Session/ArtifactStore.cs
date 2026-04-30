using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using OpenMono.Tools;

namespace OpenMono.Session;

public sealed class ArtifactStore : IDisposable
{

    public const int DefaultLargeOutputThreshold = 50_000;

    private readonly string _artifactDirectory;
    private readonly int _largeOutputThreshold;
    private readonly ConcurrentDictionary<string, ArtifactMetadata> _artifacts = new();
    private bool _disposed;

    public ArtifactStore(string sessionId, string dataDirectory, int? largeOutputThreshold = null)
    {
        _artifactDirectory = Path.Combine(dataDirectory, "artifacts", sessionId);
        _largeOutputThreshold = largeOutputThreshold ?? DefaultLargeOutputThreshold;
        Directory.CreateDirectory(_artifactDirectory);
    }

    public static ArtifactStore ForSession(SessionState session, string dataDirectory)
    {
        var sessionId = session.Id ?? Guid.NewGuid().ToString("N")[..8];
        return new ArtifactStore(sessionId, dataDirectory);
    }

    public int LargeOutputThreshold => _largeOutputThreshold;

    public ToolResult PersistAndReplace(ToolResult result, string toolName)
    {
        if (result.Class != ResultClass.Success)
            return result;

        var content = result.ModelPreview;
        if (content.Length <= _largeOutputThreshold)
            return result;

        var artifactId = GenerateArtifactId(toolName, content);
        var artifactPath = Path.Combine(_artifactDirectory, $"{artifactId}.txt");

        File.WriteAllText(artifactPath, content, Encoding.UTF8);
        var bytes = new FileInfo(artifactPath).Length;

        var metadata = new ArtifactMetadata(
            Id: artifactId,
            ToolName: toolName,
            Path: artifactPath,
            Bytes: bytes,
            CreatedAt: DateTime.UtcNow,
            ContentHash: ComputeHash(content));
        _artifacts[artifactId] = metadata;

        var artifactRef = new ArtifactRef(
            Id: artifactId,
            Kind: ClassifyArtifactKind(toolName),
            Bytes: bytes,
            Path: artifactPath);

        var truncatedPreview = BuildTruncatedPreview(content, artifactId, bytes);

        return result with
        {
            ModelPreview = truncatedPreview,
            Artifacts = [.. result.Artifacts, artifactRef]
        };
    }

    public string? GetContent(string artifactId)
    {
        if (!_artifacts.TryGetValue(artifactId, out var metadata))
            return null;

        if (!File.Exists(metadata.Path))
            return null;

        return File.ReadAllText(metadata.Path, Encoding.UTF8);
    }

    public ArtifactMetadata? GetMetadata(string artifactId) =>
        _artifacts.TryGetValue(artifactId, out var metadata) ? metadata : null;

    public IReadOnlyList<ArtifactMetadata> ListArtifacts() =>
        _artifacts.Values.ToList();

    public int CleanupOldArtifacts(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var removed = 0;

        foreach (var (id, metadata) in _artifacts)
        {
            if (metadata.CreatedAt < cutoff)
            {
                if (_artifacts.TryRemove(id, out _))
                {
                    try { File.Delete(metadata.Path); } catch {}
                    removed++;
                }
            }
        }

        return removed;
    }

    public long TotalBytes => _artifacts.Values.Sum(m => m.Bytes);

    public int Count => _artifacts.Count;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

    }

    private static string GenerateArtifactId(string toolName, string content)
    {
        var hash = ComputeHash($"{toolName}:{content}");
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        return $"{toolName.ToLowerInvariant()}_{timestamp}_{hash[..8]}";
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ClassifyArtifactKind(string toolName) => toolName switch
    {
        "FileRead" => "file_content",
        "Grep" => "grep_matches",
        "Glob" => "file_list",
        "Bash" => "command_output",
        "WebFetch" => "web_content",
        _ when toolName.StartsWith("mcp__") => "mcp_response",
        _ => "tool_output"
    };

    private static string BuildTruncatedPreview(string content, string artifactId, long bytes)
    {
        const int previewHeadLines = 20;
        const int previewTailLines = 10;

        var lines = content.Split('\n');
        var totalLines = lines.Length;

        if (totalLines <= previewHeadLines + previewTailLines + 5)
        {

            return content;
        }

        var headLines = string.Join('\n', lines.Take(previewHeadLines));
        var tailLines = string.Join('\n', lines.TakeLast(previewTailLines));
        var omittedLines = totalLines - previewHeadLines - previewTailLines;

        return $"""
            {headLines}

            ... [{omittedLines} lines omitted — full output in artifact {artifactId} ({bytes:N0} bytes)] ...

            {tailLines}
            """;
    }
}

public sealed record ArtifactMetadata(
    string Id,
    string ToolName,
    string Path,
    long Bytes,
    DateTime CreatedAt,
    string ContentHash);
