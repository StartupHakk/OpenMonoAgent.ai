namespace OpenMono.Windows.Hardware;

/// <summary>
/// Disk space probing for model downloads. Requires model size plus
/// mmproj plus 10 percent overhead before a download starts.
/// </summary>
public static class DiskDetector
{
    public const double OverheadFactor = 1.10;

    public sealed record DiskReport(
        string Directory,
        string Root,
        long FreeBytes,
        long TotalBytes);

    public static DiskReport Detect(string directory)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(directory))
            ?? throw new InvalidOperationException($"Cannot determine drive root for {directory}");
        var drive = new DriveInfo(root);
        return new DiskReport(directory, root, drive.AvailableFreeSpace, drive.TotalSize);
    }

    public static long RequiredBytes(long modelBytes, long mmprojBytes)
    {
        return (long)Math.Ceiling((modelBytes + mmprojBytes) * OverheadFactor);
    }

    public static bool HasRoom(long freeBytes, long modelBytes, long mmprojBytes)
    {
        return freeBytes >= RequiredBytes(modelBytes, mmprojBytes);
    }

    public static string DescribeBytes(long bytes)
    {
        const long gib = 1024L * 1024L * 1024L;
        if (bytes >= gib)
        {
            return $"{bytes / (double)gib:0.0} GB";
        }

        return $"{bytes / (1024.0 * 1024.0):0.0} MB";
    }
}
