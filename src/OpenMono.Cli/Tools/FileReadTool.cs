using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenMono.Permissions;
using OpenMono.Session;
using OpenMono.Utils;

namespace OpenMono.Tools;

public sealed class FileReadTool : ToolBase
{
    public override string Name => "FileRead";
    public override string Description => "Read a file from the filesystem. Returns the contents with line numbers. " +
        "For image files (png, jpg, jpeg, gif, webp), attaches the image directly so you can view and describe it. " +
        "Can also read multiple files from a cursor (e.g., from Grep results).";
    public override bool IsConcurrencySafe => true;
    public override bool IsReadOnly => true;
    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

    private static readonly ConcurrentDictionary<string, (long MtimeTicks, string ContentHash)> _readCache = new();

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("file_path", "Absolute path to the file to read")
        .AddInteger("offset", "Line number to start reading from (0-based). Ignored when head_lines or tail_lines is set.", minimum: 0)
        .AddInteger("limit", "Maximum number of lines to read. Ignored when head_lines or tail_lines is set.", minimum: 1)
        .AddInteger("head_lines", "Read only the FIRST N lines (like `head -n N`). Use instead of offset/limit for the top of a file.", minimum: 1)
        .AddInteger("tail_lines", "Read only the LAST N lines (like `tail -n N`). Use for the bottom of a file / recent log output.", minimum: 1)
        .AddString("from_cursor", "P2.6: Cursor ID from a previous tool (e.g., Grep). Reads all files in the cursor.")
        .AddInteger("max_files", "When using from_cursor, maximum number of files to read (default: 5)", minimum: 1, maximum: 20);

    public override IReadOnlyList<Capability> RequiredCapabilities(JsonElement input)
    {

        if (input.TryGetProperty("from_cursor", out _))
            return [];

        var filePath = input.TryGetProperty("file_path", out var fp) ? fp.GetString() : null;
        if (string.IsNullOrEmpty(filePath))
            return [];
        return [new FileReadCap(filePath)];
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {

        if (input.TryGetProperty("from_cursor", out var cursorProp) &&
            cursorProp.GetString() is { } cursorId)
        {
            return await ExecuteFromCursorAsync(cursorId, input, context, ct);
        }

        if (!input.TryGetProperty("file_path", out var fpProp) || fpProp.GetString() is not { } filePath)
            return ToolResult.InvalidInput("Missing file_path", "Provide either file_path or from_cursor");

        var offset = input.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;
        var limit = input.TryGetProperty("limit", out var l) ? l.GetInt32() : 2000;
        var headLines = input.TryGetProperty("head_lines", out var h) ? h.GetInt32() : 0;
        var tailLines = input.TryGetProperty("tail_lines", out var t) ? t.GetInt32() : 0;

        var resolvedPath = Path.GetFullPath(filePath, context.WorkingDirectory);

        var contentCacheDir = Path.GetFullPath(
            Path.Combine(context.Config.DataDirectory, "content-cache"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var isContentCache = resolvedPath.StartsWith(contentCacheDir, StringComparison.Ordinal);

        if (!isContentCache && PathGuard.Validate(resolvedPath, context.WorkingDirectory) is { } guardError)
            return ToolResult.Error(guardError);

        if (!File.Exists(resolvedPath))
            return ToolResult.Error($"File not found: {resolvedPath}");

        var ext = Path.GetExtension(resolvedPath).TrimStart('.').ToLowerInvariant();
        if (ImageUtils.Extensions.Contains(ext))
        {
            try
            {
                var raw = await File.ReadAllBytesAsync(resolvedPath, ct);
                var (bytes, mime) = ImageUtils.SmartResize(raw, ImageUtils.MimeFromExt(ext));
                var b64 = Convert.ToBase64String(bytes);
                var info = new FileInfo(resolvedPath);
                return ToolResult.Success($"[Image: {Path.GetFileName(resolvedPath)} ({info.Length / 1024}KB)]")
                    .WithImages([new ImagePart($"data:{mime};base64,{b64}")]);
            }
            catch (Exception ex)
            {
                return ToolResult.Error($"Error reading image: {ex.Message}");
            }
        }

        try
        {
            var fileInfo = new FileInfo(resolvedPath);
            var mtimeTicks = fileInfo.LastWriteTimeUtc.Ticks;
            var cacheKey = $"{resolvedPath}|{offset}|{limit}|{headLines}|{tailLines}";

            var lines = await File.ReadAllLinesAsync(resolvedPath, ct);
            var totalLines = lines.Length;

            if (totalLines == 0)
                return ToolResult.Success($"File is empty: {resolvedPath}");

            // Resolve the [start, count) window. head_lines / tail_lines take
            // precedence over offset/limit and let the model express `head -n N`
            // and `tail -n N` without reaching for Bash.
            int start, count;
            if (tailLines > 0)
            {
                start = Math.Max(0, totalLines - tailLines);
                count = totalLines - start;
            }
            else if (headLines > 0)
            {
                start = 0;
                count = Math.Min(headLines, totalLines);
            }
            else
            {
                start = offset;
                count = limit;
            }

            var selectedLines = lines
                .Skip(start)
                .Take(count)
                .Select((line, idx) => $"{start + idx + 1}\t{InputSanitizer.SanitizeToolOutput(line)}");

            var content = string.Join('\n', selectedLines);
            var contentHash = ComputeHash(content);

            var endLine = Math.Min(start + count, totalLines);

            if (_readCache.TryGetValue(cacheKey, out var cached) &&
                cached.MtimeTicks == mtimeTicks &&
                cached.ContentHash == contentHash)
            {
                return ToolResult.Success(
                    $"[file_unchanged: {resolvedPath}] — same content as previous read " +
                    $"(lines {start + 1}-{endLine}, {totalLines} total)");
            }

            _readCache[cacheKey] = (mtimeTicks, contentHash);

            var header = $"[{resolvedPath}] ({totalLines} lines total)";

            if (start > 0 || endLine < totalLines)
                header += $" showing lines {start + 1}-{endLine}";

            return ToolResult.Success($"{header}\n{content}");
        }
        catch (UnauthorizedAccessException)
        {
            return ToolResult.Error(
                $"Access denied: cannot read '{resolvedPath}'. " +
                "Do not attempt to change file permissions with chmod, chown, icacls, takeown, " +
                "or attrib — ask the user to grant read access manually.");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Error reading file: {ex.Message}");
        }
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes)[..16];
    }

    public static void ClearCache() => _readCache.Clear();

    public static void InvalidateCache(string resolvedPath)
    {
        var keysToRemove = _readCache.Keys.Where(k => k.StartsWith(resolvedPath + "|")).ToList();
        foreach (var key in keysToRemove)
            _readCache.TryRemove(key, out _);
    }

    private async Task<ToolResult> ExecuteFromCursorAsync(
        string cursorId, JsonElement input, ToolContext context, CancellationToken ct)
    {
        if (context.Cursors is null)
            return ToolResult.InvalidInput("Cursor store not available", "Cursor-based reads require a session with cursor support");

        var entry = context.Cursors.Get(cursorId);
        if (entry is null)
            return ToolResult.InvalidInput($"Cursor '{cursorId}' not found or expired",
                "Cursors expire after 30 minutes. Re-run the search to get a fresh cursor.");

        IReadOnlyList<string> files;
        if (entry.Data is GrepCursorData grepData)
        {
            files = grepData.Files;
        }
        else if (entry.Data is IReadOnlyList<string> fileList)
        {
            files = fileList;
        }
        else
        {
            return ToolResult.InvalidInput($"Cursor '{cursorId}' does not contain file data",
                "This cursor type cannot be used with FileRead");
        }

        if (files.Count == 0)
            return ToolResult.Success($"Cursor '{cursorId}' contains no files");

        var maxFiles = input.TryGetProperty("max_files", out var mf) ? mf.GetInt32() : 5;
        maxFiles = Math.Clamp(maxFiles, 1, 20);
        var limit = input.TryGetProperty("limit", out var l) ? l.GetInt32() : 500;

        var filesToRead = files.Take(maxFiles).ToList();
        var results = new StringBuilder();
        results.AppendLine($"Reading {filesToRead.Count} file(s) from cursor '{cursorId}':");
        results.AppendLine();

        var fileContents = new List<(string Path, string Content, int Lines)>();

        foreach (var file in filesToRead)
        {
            ct.ThrowIfCancellationRequested();

            if (!File.Exists(file))
            {
                results.AppendLine($"--- {file} (not found) ---");
                continue;
            }

            try
            {
                var lines = await File.ReadAllLinesAsync(file, ct);
                var totalLines = lines.Length;
                var selectedLines = lines.Take(limit).Select((line, idx) => $"{idx + 1}\t{line}");
                var content = string.Join('\n', selectedLines);

                var truncated = totalLines > limit ? $" (showing first {limit} of {totalLines} lines)" : "";
                results.AppendLine($"--- {file}{truncated} ---");
                results.AppendLine(content);
                results.AppendLine();

                fileContents.Add((file, content, totalLines));
            }
            catch (Exception ex)
            {
                results.AppendLine($"--- {file} (error: {ex.Message}) ---");
            }
        }

        if (files.Count > maxFiles)
        {
            results.AppendLine($"... and {files.Count - maxFiles} more file(s). Increase max_files to read more.");
        }

        return ToolResult.SuccessWithPayload(
            results.ToString().TrimEnd(),
            new { files_read = fileContents.Count, cursor_id = cursorId, file_contents = fileContents });
    }
}
