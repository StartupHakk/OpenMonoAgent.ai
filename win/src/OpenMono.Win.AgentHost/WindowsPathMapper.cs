namespace OpenMono.Win.AgentHost;

// Maps between Windows host paths and file:// URIs for host-initiated ACP
// calls (launcher and scripts posting turns that reference Windows files).
// The agent itself runs in the Linux container and keeps its own
// /workspace-relative handling; this mapper is only for the host side.
// Manual segment escaping keeps behavior identical on Windows and on Linux CI.
public static class WindowsPathMapper
{
    public static string ToFileUri(string windowsPath)
    {
        if (string.IsNullOrWhiteSpace(windowsPath))
        {
            throw new ArgumentException("Path must not be empty.", nameof(windowsPath));
        }

        var forward = windowsPath.Replace('\\', '/');
        string withRoot;
        if (forward.Length >= 2 && char.IsLetter(forward[0]) && forward[1] == ':')
        {
            withRoot = "/" + forward;
        }
        else if (forward.StartsWith("//"))
        {
            // UNC path: file://server/share/...
            return "file:" + EscapeSegments(forward);
        }
        else
        {
            withRoot = forward.StartsWith('/') ? forward : "/" + forward;
        }

        return "file://" + EscapeSegments(withRoot);
    }

    public static string FromFileUri(string fileUri)
    {
        if (!fileUri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Not a file URI.", nameof(fileUri));
        }

        var path = fileUri["file://".Length..];
        var unescaped = Uri.UnescapeDataString(path);

        // Strip the leading slash from /C:/... drive forms.
        if (unescaped.Length >= 3 && unescaped[0] == '/' && char.IsLetter(unescaped[1]) && unescaped[2] == ':')
        {
            unescaped = unescaped[1..];
        }

        return OperatingSystem.IsWindows()
            ? unescaped.Replace('/', '\\')
            : unescaped;
    }

    private static string EscapeSegments(string path)
    {
        var parts = path.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0)
            {
                continue;
            }

            // Keep the drive colon (C:) readable; escape everything else.
            if (i == 1 && parts[i].Length == 2 && char.IsLetter(parts[i][0]) && parts[i][1] == ':')
            {
                continue;
            }

            parts[i] = Uri.EscapeDataString(parts[i]);
        }

        return string.Join("/", parts);
    }
}
