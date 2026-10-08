namespace OpenMono.Windows.AgentHost;

/// <summary>
/// Windows path to file URI mapping. OMA LspClient concatenates file://
/// naively, which breaks on drive letters, backslashes, and spaces, so the
/// Windows host converts first with System.Uri and passes correct absolute
/// URIs. OMA LspClient.cs is not edited.
/// </summary>
public static class LspPathMapper
{
    public static string ToFileUri(string path)
    {
        var full = Path.GetFullPath(path);
        return new Uri(full).AbsoluteUri;
    }

    public static string FromFileUri(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile)
        {
            return parsed.LocalPath;
        }

        const string prefix = "file://";
        if (uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var remainder = uri[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            return Uri.UnescapeDataString(remainder);
        }

        return uri;
    }

    public static string NormalizeWorkspace(string workspace) =>
        Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
}
