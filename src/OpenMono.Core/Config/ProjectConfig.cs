namespace OpenMono.Config;

public static class ProjectConfig
{
    private const string FileName = "OPENMONO.md";
    private const int MaxLines = 200;
    private const int MaxBytes = 25_000;

    public static string? Load(string workingDirectory)
    {

        var dir = new DirectoryInfo(workingDirectory);
        while (dir is not null)
        {
            var filePath = Path.Combine(dir.FullName, FileName);
            if (File.Exists(filePath))
                return ReadTruncated(filePath);

            dir = dir.Parent;
        }

        return null;
    }

    private static string ReadTruncated(string path)
    {
        var content = File.ReadAllText(path);

        if (content.Length > MaxBytes)
            content = content[..MaxBytes] + "\n\n... (truncated at 25KB limit)";

        var lines = content.Split('\n');
        if (lines.Length > MaxLines)
        {
            content = string.Join('\n', lines.Take(MaxLines));
            content += $"\n\n... (truncated at {MaxLines} line limit)";
        }

        return content;
    }
}
