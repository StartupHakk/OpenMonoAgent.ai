using System.Text.Json;
using OpenMono.Permissions;
using OpenMono.Utils;

namespace OpenMono.Tools;

public sealed class FileEditTool : ToolBase
{
    private static readonly object EditItemSchema = new
    {
        type = "object",
        properties = new
        {
            old_string = new { type = "string", description = "The exact text to find and replace" },
            new_string = new { type = "string", description = "The replacement text" },
            replace_all = new { type = "boolean", description = "Replace all occurrences of old_string within this hunk (default: false)" },
        },
        required = new[] { "old_string", "new_string" },
    };

    public override string Name => "FileEdit";
    public override string Description => "Perform one or more exact string replacements in a file, applied atomically (all or nothing). " +
        "Use old_string/new_string for a single hunk, or edits for several hunks in one file — each later hunk is matched against the file as left by the earlier ones.";

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("file_path", "Absolute path to the file to edit")
        .AddString("old_string", "The exact text to find and replace (single-hunk form; omit when using edits)")
        .AddString("new_string", "The replacement text (single-hunk form; omit when using edits)")
        .AddBoolean("replace_all", "Replace all occurrences (single-hunk form, default: false)")
        .AddArray("edits", "Multiple find-and-replace hunks to apply atomically, in order. Use instead of old_string/new_string for multi-hunk edits in one file.", EditItemSchema)
        .Require("file_path");

    public override IReadOnlyList<Capability> RequiredCapabilities(JsonElement input)
    {
        var filePath = input.TryGetProperty("file_path", out var fp) ? fp.GetString() : null;
        if (string.IsNullOrEmpty(filePath))
            return [];
        return [new FileWriteCap(filePath, "modify")];
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var filePath = input.GetProperty("file_path").GetString()!;

        var hunks = ParseHunks(input);
        if (hunks is null)
            return ToolResult.Error(
                "Provide either old_string/new_string for a single edit, or a non-empty edits array for multiple hunks.");

        for (var i = 0; i < hunks.Count; i++)
        {
            var h = hunks[i];
            var label = hunks.Count == 1 ? "" : $"edits[{i}]: ";
            if (string.IsNullOrEmpty(h.OldString))
                return ToolResult.Error(
                    $"{label}old_string must not be empty. Use FileWrite to create a new file, " +
                    "or supply non-empty text to find and replace.");

            if (h.OldString == h.NewString)
                return ToolResult.Error($"{label}old_string and new_string are identical — nothing to replace.");
        }

        var resolvedPath = Path.GetFullPath(filePath, context.WorkingDirectory);

        if (PathGuard.Validate(resolvedPath, context.WorkingDirectory) is { } guardError)
            return ToolResult.Error(guardError);

        if (!File.Exists(resolvedPath))
            return ToolResult.Error($"File not found: {resolvedPath}");

        try
        {
            var originalContent = await File.ReadAllTextAsync(resolvedPath, ct);
            var content = originalContent;
            var totalReplacements = 0;
            var secretMessages = new List<string>();
            var appliedNewStrings = new string[hunks.Count];

            for (var i = 0; i < hunks.Count; i++)
            {
                var h = hunks[i];
                var label = hunks.Count == 1 ? "" : $"edits[{i}]: ";
                var occurrences = CountOccurrences(content, h.OldString);

                if (occurrences == 0)
                    return ToolResult.Error(
                        $"{label}old_string not found in {resolvedPath}" +
                        (i > 0 ? " after applying the earlier edit(s)" : ""));

                if (occurrences > 1 && !h.ReplaceAll)
                    return ToolResult.Error(
                        $"{label}old_string found {occurrences} times in {resolvedPath}. " +
                        "Provide more context to make it unique, or set replace_all=true.");

                var guard = SecretScanner.Guard(h.NewString, context.Config.SecretWrites);
                if (guard.Blocked)
                    return ToolResult.PermissionDenied(guard.Message);
                if (!string.IsNullOrEmpty(guard.Message))
                    secretMessages.Add(guard.Message);

                appliedNewStrings[i] = guard.Content;
                content = h.ReplaceAll
                    ? content.Replace(h.OldString, guard.Content)
                    : ReplaceFirst(content, h.OldString, guard.Content);

                totalReplacements += h.ReplaceAll ? occurrences : 1;
            }

            context.FileHistory?.RecordBefore(resolvedPath, Name, context.Session.Messages.Count);

            await File.WriteAllTextAsync(resolvedPath, content, ct);

            context.FileHistory?.RecordAfter(resolvedPath);

            var secretMessage = string.Concat(secretMessages);
            var diff = hunks.Count == 1
                ? InlineDiff.FromEdit(hunks[0].OldString, appliedNewStrings[0], resolvedPath)
                : InlineDiff.FromOverwrite(originalContent, content, resolvedPath);

            var summary = hunks.Count == 1
                ? $"Replaced {totalReplacements} occurrence(s) in {resolvedPath}{secretMessage}"
                : $"Applied {hunks.Count} edit(s), replaced {totalReplacements} occurrence(s) total in {resolvedPath}{secretMessage}";

            return ToolResult.Success(summary).WithDiff(diff);
        }
        catch (UnauthorizedAccessException)
        {
            return ToolResult.Error(DiagnoseWriteFailure(resolvedPath));
        }
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070020) ||
                                     ex.Message.Contains("being used by another process"))
        {
            return ToolResult.Error($"Cannot edit '{resolvedPath}': file is locked by another process.");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Error editing file: {ex.Message}");
        }
    }

    private static List<EditHunk>? ParseHunks(JsonElement input)
    {
        if (input.TryGetProperty("edits", out var editsEl) && editsEl.ValueKind == JsonValueKind.Array)
        {
            var hunks = new List<EditHunk>();
            foreach (var e in editsEl.EnumerateArray())
            {
                var oldString = e.TryGetProperty("old_string", out var o) ? o.GetString() ?? "" : "";
                var newString = e.TryGetProperty("new_string", out var n) ? n.GetString() ?? "" : "";
                var replaceAll = e.TryGetProperty("replace_all", out var ra) && ra.GetBoolean();
                hunks.Add(new EditHunk(oldString, newString, replaceAll));
            }
            return hunks.Count > 0 ? hunks : null;
        }

        if (!input.TryGetProperty("old_string", out var oldEl) || !input.TryGetProperty("new_string", out var newEl))
            return null;

        var singleReplaceAll = input.TryGetProperty("replace_all", out var ra2) && ra2.GetBoolean();
        return [new EditHunk(oldEl.GetString() ?? "", newEl.GetString() ?? "", singleReplaceAll)];
    }

    private readonly record struct EditHunk(string OldString, string NewString, bool ReplaceAll);

    private static string DiagnoseWriteFailure(string path)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).IsReadOnly)
            {
                return OperatingSystem.IsWindows()
                    ? $"Cannot edit '{path}': file is read-only. Run in your terminal: attrib -r \"{path}\""
                    : $"Cannot edit '{path}': file has no write permission. Run in your terminal: chmod u+w {path}";
            }
        }
        catch (Exception ex) { OpenMono.Utils.Log.Debug($"Read-only probe failed for '{path}': {ex.Message}"); }

        return $"Cannot edit '{path}': access denied. Check ownership with: ls -la {path}";
    }

    private static int CountOccurrences(string text, string search)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(search, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += search.Length;
        }
        return count;
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        if (index < 0) return text;
        return string.Concat(text.AsSpan(0, index), newValue, text.AsSpan(index + oldValue.Length));
    }
}
