namespace OpenMono.Win.Foundry;

// Result of the CUDA/cuDNN visibility check. Foundry Local does not ship these
// runtimes, so a missing DLL is a setup error with a remediation, never a crash.
public sealed record CudaCheckResult(
    bool SmiFound,
    bool CudaDllFound,
    bool CudnnDllFound,
    IReadOnlyList<string> Issues,
    string Remediation)
{
    public bool Ok => SmiFound && CudaDllFound && CudnnDllFound && Issues.Count == 0;
}

public sealed record CudaCheckInput(
    IReadOnlyList<string> PathDirectories,
    Func<string, IReadOnlyList<string>> ListFiles);

// Checks that the NVIDIA driver and the CUDA/cuDNN DLLs are discoverable.
// All file access goes through CudaCheckInput, so tests inject fake trees.
public static class CudaPrerequisiteChecker
{
    public const string RemediationText =
        "Install CUDA and a compatible cuDNN runtime per the Foundry Local 0.13.0+ " +
        "release notes, then add their bin directories to PATH and open a new terminal. " +
        "Foundry Local does not download these runtimes for you.";

    public static CudaCheckResult Check(CudaCheckInput input, bool smiFound)
    {
        var issues = new List<string>();
        var cudaFound = false;
        var cudnnFound = false;
        string? cudaDir = null;
        string? cudnnDir = null;

        if (!smiFound)
        {
            issues.Add("nvidia-smi was not found: no NVIDIA driver on PATH.");
        }

        foreach (var dir in input.PathDirectories)
        {
            IReadOnlyList<string> files;
            try
            {
                files = input.ListFiles(dir);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (!cudaFound && name.StartsWith("cublas64", StringComparison.OrdinalIgnoreCase))
                {
                    cudaFound = true;
                    cudaDir = dir;
                }

                if (!cudnnFound && name.StartsWith("cudnn64", StringComparison.OrdinalIgnoreCase))
                {
                    cudnnFound = true;
                    cudnnDir = dir;
                }
            }
        }

        if (!cudaFound)
        {
            issues.Add("No cublas64*.dll found on PATH: CUDA runtime is not discoverable.");
        }

        if (!cudnnFound)
        {
            issues.Add("No cudnn64*.dll found on PATH: cuDNN runtime is not discoverable.");
        }

        var remediation = issues.Count == 0
            ? "CUDA and cuDNN DLLs resolve "
                + $"(CUDA in {cudaDir}, cuDNN in {cudnnDir}). No action needed."
            : RemediationText;

        return new CudaCheckResult(smiFound, cudaFound, cudnnFound, issues, remediation);
    }
}
