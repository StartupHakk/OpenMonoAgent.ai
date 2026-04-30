using FluentAssertions;
using OpenMono.Playbooks;

namespace OpenMono.Tests.Playbooks;

public class PlaybookRegistryTests
{
    [Fact]
    public void Register_And_Resolve_Works()
    {
        var registry = new PlaybookRegistry();
        registry.Register(new PlaybookDefinition { Name = "commit", Description = "Smart commit" });

        registry.Resolve("commit").Should().NotBeNull();
        registry.Resolve("commit")!.Name.Should().Be("commit");
    }

    [Fact]
    public void Resolve_CaseInsensitive()
    {
        var registry = new PlaybookRegistry();
        registry.Register(new PlaybookDefinition { Name = "Commit", Description = "Smart commit" });

        registry.Resolve("commit").Should().NotBeNull();
        registry.Resolve("COMMIT").Should().NotBeNull();
    }

    [Fact]
    public void Resolve_Unknown_ReturnsNull()
    {
        var registry = new PlaybookRegistry();
        registry.Resolve("nonexistent").Should().BeNull();
    }

    [Fact]
    public void MatchTrigger_MatchesPattern()
    {
        var registry = new PlaybookRegistry();
        registry.Register(new PlaybookDefinition
        {
            Name = "review",
            Description = "Code review",
            Trigger = TriggerMode.Auto,
            TriggerPatterns = ["review *", "code review *"],
        });

        var match = registry.MatchTrigger("review my PR");
        match.Should().NotBeNull();
        match!.Name.Should().Be("review");
    }

    [Fact]
    public void MatchTrigger_ManualOnly_NoMatch()
    {
        var registry = new PlaybookRegistry();
        registry.Register(new PlaybookDefinition
        {
            Name = "commit",
            Description = "Smart commit",
            Trigger = TriggerMode.Manual,
            TriggerPatterns = ["commit *"],
        });

        registry.MatchTrigger("commit this change").Should().BeNull();
    }

    [Fact]
    public void MatchTrigger_NoMatch_ReturnsNull()
    {
        var registry = new PlaybookRegistry();
        registry.Register(new PlaybookDefinition
        {
            Name = "review",
            Description = "Code review",
            Trigger = TriggerMode.Auto,
            TriggerPatterns = ["review *"],
        });

        registry.MatchTrigger("build the project").Should().BeNull();
    }

    [Fact]
    public void RegisterAll_RegistersMultiple()
    {
        var registry = new PlaybookRegistry();
        registry.RegisterAll([
            new PlaybookDefinition { Name = "commit", Description = "Commit" },
            new PlaybookDefinition { Name = "review", Description = "Review" },
        ]);

        registry.All.Should().HaveCount(2);
    }
}
