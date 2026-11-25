using FluentAssertions;
using OpenMono.Session;
using OpenMono.Utils;

namespace OpenMono.Tests.Session;

public class SessionSettingsTests
{
    private static ModelReasoningProfile EffortProfile(params string[] levels)
        => new()
        {
            Kind = ReasoningKind.EffortLevels,
            DefaultEnabled = false,
            DefaultLevel = "off",
            Levels = levels,
        };

    private static readonly ModelReasoningProfile BinaryProfile = new()
    {
        Kind = ReasoningKind.BinaryToggle,
        DefaultEnabled = false,
        DefaultLevel = "off",
    };

    [Fact]
    public async Task CycleThinking_AdvancesAndPersistsAndBumpsRevision()
    {
        var session = new SessionState { Meta = { ThinkingLevel = "off" } };
        var saves = 0;
        var events = new List<StateChangeKind>();
        var settings = SessionSettings.Attach(
            session, "tui",
            persist: _ => { saves++; return Task.CompletedTask; },
            reasoningProfile: () => EffortProfile("off", "low", "medium", "xhigh"),
            onChanged: e => events.Add(e.Kind));

        var result = await settings.CycleThinkingAsync();

        result.Applied.Should().BeTrue();
        session.Meta.ThinkingLevel.Should().Be("low");
        session.Meta.ThinkingEnabled.Should().BeTrue();
        saves.Should().Be(1);
        events.Should().Equal(StateChangeKind.Thinking);
    }

    [Fact]
    public async Task CycleThinking_WrapsAroundToOff()
    {
        var session = new SessionState { Meta = { ThinkingLevel = "xhigh" } };
        var settings = new SessionSettings(
            session, "tui", reasoningProfile: () => EffortProfile("off", "low", "medium", "xhigh"));

        await settings.CycleThinkingAsync();

        session.Meta.ThinkingLevel.Should().Be("off");
        session.Meta.ThinkingEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task SetThinking_ClampsUnknownLevelToDefault()
    {
        var session = new SessionState { Meta = { ThinkingLevel = "medium" } };
        var settings = new SessionSettings(
            session, "tui", reasoningProfile: () => EffortProfile("off", "low", "medium", "xhigh"));

        // "ultra" isn't a real level and the current "medium" IS advertised,
        // so the session keeps medium and reports the bad input.
        var result = await settings.SetThinkingAsync("ultra");

        result.Applied.Should().BeFalse();
        result.Error.Should().Contain("Unknown level");
        session.Meta.ThinkingLevel.Should().Be("medium");
    }

    [Fact]
    public async Task SetThinking_BinaryToggleIgnoresNamedLevel()
    {
        var session = new SessionState { Meta = { ThinkingEnabled = false, ThinkingLevel = "off" } };
        var settings = new SessionSettings(session, "tui", reasoningProfile: () => BinaryProfile);

        await settings.SetThinkingAsync("high");

        // Binary models have no levels; any non-off value turns thinking on.
        session.Meta.ThinkingLevel.Should().Be("on");
        session.Meta.ThinkingEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task ClampThinking_DowngradesLevelServerNoLongerAdvertises()
    {
        var session = new SessionState { Meta = { ThinkingLevel = "xhigh", ThinkingEnabled = true } };
        var saves = 0;
        var settings = new SessionSettings(
            session, "tui",
            persist: _ => { saves++; return Task.CompletedTask; },
            // New server only advertises off/low — xhigh must be clamped to default.
            reasoningProfile: () => EffortProfile("off", "low"));

        var clamped = await settings.ClampThinkingToCapabilitiesAsync();

        clamped.Should().BeTrue();
        session.Meta.ThinkingLevel.Should().Be("off");
        session.Meta.ThinkingEnabled.Should().BeFalse();
        saves.Should().Be(1);
    }

    [Fact]
    public async Task ClampThinking_NoOpWhenLevelStillAdvertised()
    {
        var session = new SessionState { Meta = { ThinkingLevel = "low", ThinkingEnabled = true } };
        var saves = 0;
        var settings = new SessionSettings(
            session, "tui",
            persist: _ => { saves++; return Task.CompletedTask; },
            reasoningProfile: () => EffortProfile("off", "low", "medium", "xhigh"));

        var clamped = await settings.ClampThinkingToCapabilitiesAsync();

        clamped.Should().BeFalse();
        session.Meta.ThinkingLevel.Should().Be("low");
        saves.Should().Be(0);
    }

    [Fact]
    public async Task SetPlanMode_BumpsRevisionAndBroadcasts()
    {
        var session = new SessionState();
        var events = new List<(StateChangeKind Kind, object? Payload)>();
        var settings = SessionSettings.Attach(
            session, "acp",
            onChanged: e => events.Add((e.Kind, e.Payload)));

        await settings.SetPlanModeAsync(true);

        session.Meta.PlanMode.Should().BeTrue();
        session.Meta.Revision.Should().Be(1);
        session.Meta.LastWriter.Should().Be("acp");
        events.Should().ContainSingle().Which.Kind.Should().Be(StateChangeKind.Mode);
    }

    [Fact]
    public async Task SetModel_DoesNotChangeThinking_ThatIsClampSeparately()
    {
        var session = new SessionState { Model = "a", Meta = { ThinkingLevel = "xhigh", ThinkingEnabled = true } };
        var settings = new SessionSettings(
            session, "tui",
            reasoningProfile: () => EffortProfile("off", "low", "medium", "xhigh"));

        await settings.SetModelAsync("b");

        session.Model.Should().Be("b");
        // SetModel itself leaves thinking alone (caller re-clamps after re-probe).
        session.Meta.ThinkingLevel.Should().Be("xhigh");
    }

    [Fact]
    public async Task PersistFailure_DoesNotThrowAndStillBroadcasts()
    {
        var session = new SessionState();
        var broadcast = false;
        var settings = SessionSettings.Attach(
            session, "tui",
            persist: _ => throw new IOException("disk gone"),
            onChanged: _ => broadcast = true);

        // A failed write-through must not blow up the caller (e.g. a live turn) —
        // it logs and continues.
        await settings.SetAutoApproveAsync(true);

        session.Meta.AutoApproveWrites.Should().BeTrue();
        broadcast.Should().BeTrue();
    }
}
