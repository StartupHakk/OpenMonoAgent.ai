namespace OpenMono.Utils;

public static class FileSearcher
{
    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {

        "node_modules", ".next", ".nuxt", ".svelte-kit", "dist", ".output",
        ".turbo", "bower_components", ".cache", ".parcel-cache", ".eslintcache",

        "bin", "obj", "publish", "artifacts", ".vs",

        "target",

        "__pycache__", ".pytest_cache", ".mypy_cache", "venv", ".venv", "env",

        ".gradle", ".idea", "build",

        "vendor", ".bundle",

        "coverage", ".coverage", "tmp", "temp", ".tmp", "out",

        ".vscode",

        ".git", ".hg", ".svn",

        ".dart_tool", ".flutter",

        "Pods", "DerivedData",

        "packages",
    };

    public static List<string> Search(string root, string query, int maxResults = 12)
    {
        var results = new List<string>(maxResults);
        if (!Directory.Exists(root)) return results;

        foreach (var file in EnumerateProjectFiles(root))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (Matches(rel, query))
            {
                results.Add(rel);
                if (results.Count >= maxResults) break;
            }
        }
        return results;
    }

    private static bool Matches(string relPath, string query)
    {
        if (string.IsNullOrEmpty(query)) return true;
        return relPath.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateProjectFiles(string dir)
    {
        string[] entries;
        try { entries = Directory.GetFileSystemEntries(dir); }
        catch { yield break; }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (Directory.Exists(entry))
            {
                if (!ExcludedDirs.Contains(name))
                    foreach (var f in EnumerateProjectFiles(entry))
                        yield return f;
            }
            else
            {
                yield return entry;
            }
        }
    }
}
