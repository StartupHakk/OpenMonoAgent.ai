namespace OpenMono.Utils;

public static class PathUtils
{
    public static string NormalizePath(string path, string workingDirectory) =>
        Path.GetFullPath(path, workingDirectory);

    public static string GetRelativePath(string fullPath, string basePath) =>
        Path.GetRelativePath(basePath, fullPath);

    public static bool IsSubPathOf(string path, string basePath)
    {
        var normalizedPath = Path.GetFullPath(path);
        var normalizedBase = Path.GetFullPath(basePath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return normalizedPath.StartsWith(normalizedBase, comparison);
    }
}
