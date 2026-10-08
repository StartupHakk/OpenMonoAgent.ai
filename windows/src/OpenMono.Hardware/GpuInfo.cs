namespace OpenMono.Windows.Hardware;

/// <summary>
/// GPU vendor classification used for llama-server flavor selection.
/// </summary>
public enum GpuVendor
{
    Unknown,
    Nvidia,
    Amd,
    Intel,
}

/// <summary>
/// One detected GPU adapter.
/// </summary>
public sealed record GpuInfo(
    string Name,
    GpuVendor Vendor,
    long DedicatedBytes,
    long SharedBytes,
    string? DriverVersion);
