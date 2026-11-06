namespace OpenMono.Win.Foundry;

// Small process record so CLI consumers stay testable behind ICliRunner.
public sealed record CliResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

// Abstraction over spawning child processes. Production code uses
// ProcessCliRunner; tests use FakeCliRunner (in the test project).
public interface ICliRunner
{
    Task<CliResult> RunAsync(string fileName, string arguments, CancellationToken ct = default);
}
