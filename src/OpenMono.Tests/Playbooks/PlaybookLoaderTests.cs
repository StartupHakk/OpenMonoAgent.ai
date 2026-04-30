using FluentAssertions;
using OpenMono.Playbooks;

namespace OpenMono.Tests.Playbooks;

public class PlaybookLoaderTests : IDisposable
{
    private readonly string _tempDir;

    public PlaybookLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void LoadAll_EmptyDirectory_ReturnsEmpty()
    {
        var loader = new PlaybookLoader([_tempDir]);
        loader.LoadAll().Should().BeEmpty();
    }

    [Fact]
    public void LoadAll_NoPlaybookFile_Skips()
    {
        var playbookDir = Path.Combine(_tempDir, "my-playbook");
        Directory.CreateDirectory(playbookDir);

        var loader = new PlaybookLoader([_tempDir]);
        loader.LoadAll().Should().BeEmpty();
    }

    [Fact]
    public void LoadAll_WithFrontmatter_ParsesCorrectly()
    {
        var playbookDir = Path.Combine(_tempDir, "commit");
        Directory.CreateDirectory(playbookDir);
        File.WriteAllText(Path.Combine(playbookDir, "PLAYBOOK.md"), """
            ---
            name: commit
            version: 2.0.0
            description: Smart git commit
            trigger: manual
            user-invocable: true
            ---

            You are a commit assistant.
            """);

        var loader = new PlaybookLoader([_tempDir]);
        var playbooks = loader.LoadAll();

        playbooks.Should().HaveCount(1);
        playbooks[0].Name.Should().Be("commit");
        playbooks[0].Version.Should().Be("2.0.0");
        playbooks[0].Description.Should().Be("Smart git commit");
        playbooks[0].Trigger.Should().Be(TriggerMode.Manual);
    }

    [Fact]
    public void LoadAll_WithoutFrontmatter_UsesDirectoryName()
    {
        var playbookDir = Path.Combine(_tempDir, "simple");
        Directory.CreateDirectory(playbookDir);
        File.WriteAllText(Path.Combine(playbookDir, "PLAYBOOK.md"), """
            You are a simple playbook without frontmatter.
            """);

        var loader = new PlaybookLoader([_tempDir]);
        var playbooks = loader.LoadAll();

        playbooks.Should().HaveCount(1);
        playbooks[0].Name.Should().Be("simple");
    }

    [Fact]
    public void LoadAll_WithSteps_ParsesStepsCorrectly()
    {
        var playbookDir = Path.Combine(_tempDir, "file-scan");
        Directory.CreateDirectory(playbookDir);
        File.WriteAllText(Path.Combine(playbookDir, "PLAYBOOK.md"), """
            ---
            name: file-scan
            version: 1.0.0
            description: Creates files then greps output.
            steps:
              - id: create-files
                inline-prompt: Run the create-files script.
                script: scripts/create-files.sh
                gate: None
              - id: grep-scan
                inline-prompt: Run the grep script and report results.
                script: scripts/scan.sh
                gate: None
                requires:
                  - create-files
            ---

            You are a workspace assistant.
            """);

        var loader = new PlaybookLoader([_tempDir]);
        var playbooks = loader.LoadAll();

        playbooks.Should().HaveCount(1);
        var pb = playbooks[0];
        pb.Steps.Should().HaveCount(2);

        pb.Steps[0].Id.Should().Be("create-files");
        pb.Steps[0].Script.Should().Be("scripts/create-files.sh");
        pb.Steps[0].Gate.Should().Be(GateType.None);
        pb.Steps[0].Requires.Should().BeEmpty();

        pb.Steps[1].Id.Should().Be("grep-scan");
        pb.Steps[1].Script.Should().Be("scripts/scan.sh");
        pb.Steps[1].Requires.Should().ContainSingle("create-files");
    }

    [Fact]
    public void LoadAll_NonExistentPath_Skips()
    {
        var loader = new PlaybookLoader(["/nonexistent/path"]);
        loader.LoadAll().Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
