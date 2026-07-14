using System.Text.RegularExpressions;
using OpenMono.Session;

namespace OpenMono.Utils;

public static class FileReferenceResolver
{
    /// <summary>
    /// Transforms relative @ file references to absolute paths by prepending the working directory.
    /// E.g., "@openmono-agent/file.md" becomes "@/workspace/openmono-agent/file.md"
    ///
    /// This is the PREFERRED approach for handling @ file references:
    /// - UI sends raw @ references with relative paths
    /// - Server transforms them to absolute paths with /workspace prefix
    /// - Agent receives unambiguous absolute paths
    /// - Agent calls FileRead to load files (per system prompt)
    /// - Consistent behavior across TUI and extension
    ///
    /// Only transforms relative paths (those not starting with /). Absolute paths are left unchanged.
    /// </summary>
    public static string TransformRelativeReferences(string input, string workingDirectory)
    {
        var pattern = @"@([\w/\\.\-]+)";
        var matches = Regex.Matches(input, pattern);


        var workDirNorm = Path.GetFullPath(workingDirectory).TrimEnd(Path.DirectorySeparatorChar);

        var result = Regex.Replace(input, pattern, match =>
        {
            var relPath = match.Groups[1].Value;
            var originalRef = match.Value;

            // If already absolute (starts with /), validate and leave it alone
            if (Path.IsPathRooted(relPath))
            {
                var absPath = Path.GetFullPath(relPath);

                // Validate it's within workspace
                if (!IsWithinWorkspace(absPath, workDirNorm))
                    return match.Value;

                if (!File.Exists(absPath))
                    return match.Value;

                return match.Value;
            }

            // Transform relative path to absolute
            var transformed = $"@{workingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)}/{relPath.Replace('\\', '/')}";
            var resolvedPath = Path.GetFullPath(Path.Combine(workingDirectory, relPath));

            // Validate the transformed path
            if (!IsWithinWorkspace(resolvedPath, workDirNorm))
                return match.Value;

            if (!File.Exists(resolvedPath))
                return match.Value;

            return transformed;
        });

        return result;
    }

    private static bool IsWithinWorkspace(string resolvedPath, string workspaceDirNorm)
    {
        var normalizedPath = Path.GetFullPath(resolvedPath).TrimEnd(Path.DirectorySeparatorChar);
        return normalizedPath.StartsWith(workspaceDirNorm, StringComparison.Ordinal) ||
               normalizedPath.Equals(workspaceDirNorm, StringComparison.Ordinal);
    }

    /// <summary>
    /// [DEPRECATED - kept for reference]
    ///
    /// This function preprocesses @ file references by reading files server-side and injecting their contents.
    /// We do NOT use this approach anymore because:
    /// - The system prompt explicitly instructs the agent to use FileRead tool
    /// - We want consistent behavior: agent calls FileRead, not server-side preprocessing
    /// - This allows the LLM to see files as tool calls it decides to make, not pre-injected content
    ///
    /// Instead, use TransformRelativeReferences() to convert relative paths to absolute paths,
    /// and let the agent call FileRead to load the files.
    ///
    /// Original behavior (kept for reference):
    /// - Resolves paths, validates they're within the workspace
    /// - For images (png, jpg, jpeg, gif, webp): returns them as ImagePart objects
    /// - For text files: injects them into the message as &lt;file&gt; tags
    /// - Removes @ references from the original text
    /// </summary>
    [Obsolete("Use TransformRelativeReferences instead. This function preprocesses files server-side; we want the agent to call FileRead instead.")]
    public static (string text, List<ImagePart>? images) ProcessFileReferences(string input, string workingDirectory)
    {
        var pattern = @"@([\w/\\.\-]+)";
        var matches = Regex.Matches(input, pattern);
        if (matches.Count == 0) return (input, null);

        var workDirNorm = Path.GetFullPath(workingDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var injections = new System.Text.StringBuilder();
        List<ImagePart>? images = null;
        var resolved = 0;

        foreach (Match m in matches)
        {
            var relPath = m.Groups[1].Value.Replace('\\', '/');
            var fullPath = Path.IsPathRooted(relPath)
                ? Path.GetFullPath(relPath)
                : Path.GetFullPath(Path.Combine(workingDirectory, relPath));

            // Validate path is within workspace
            if (!fullPath.StartsWith(workDirNorm + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !fullPath.Equals(workDirNorm, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!File.Exists(fullPath)) continue;

            try
            {
                var ext = Path.GetExtension(relPath).TrimStart('.').ToLower();

                // Handle images
                if (ext is "png" or "jpg" or "jpeg" or "gif" or "webp")
                {
                    var raw = File.ReadAllBytes(fullPath);
                    var (imageBytes, mime) = ImageUtils.SmartResize(raw, ImageUtils.MimeFromExt(ext));
                    var b64 = Convert.ToBase64String(imageBytes);
                    (images ??= []).Add(new ImagePart($"data:{mime};base64,{b64}"));
                    resolved++;
                }
                // Handle text files
                else
                {
                    var contents = File.ReadAllText(fullPath);
                    injections.AppendLine($"<file path=\"{relPath}\">");
                    if (!string.IsNullOrEmpty(ext)) injections.AppendLine($"```{ext}");
                    injections.AppendLine(contents);
                    if (!string.IsNullOrEmpty(ext)) injections.AppendLine("```");
                    injections.AppendLine("</file>");
                    resolved++;
                }
            }
            catch { }
        }

        if (resolved == 0) return (input, null);

        // Remove @ references from input and prepend file contents
        var cleaned = Regex.Replace(input, pattern, "").Trim();
        var text = injections.Length > 0 ? injections.ToString() + "\n" + cleaned : cleaned;
        return (text, images);
    }
}
