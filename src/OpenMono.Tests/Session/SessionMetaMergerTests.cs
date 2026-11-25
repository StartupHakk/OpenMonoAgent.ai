using FluentAssertions;
using OpenMono.Session;

namespace OpenMono.Tests.Session;

public class SessionMetaMergerTests
{
    [Fact]
    public void Merge_HigherRevisionIncoming_WinsAllSettings()
    {
        var live = new SessionState();
        live.Meta.PlanMode = true;
        live.Meta.ThinkingLevel = "off";
        live.Meta.HostEnabled = false;
        live.Meta.Revision = 1;
        live.Model = "old-model";

        var incoming = new SessionState();
        incoming.Meta.PlanMode = false;
        incoming.Meta.ThinkingLevel = "medium";
        incoming.Meta.HostEnabled = true;
        incoming.Meta.Revision = 5;
        incoming.Model = "new-model";

        var changed = SessionMetaMerger.Merge(live, incoming);

        changed.Should().BeTrue();
        live.Meta.PlanMode.Should().BeFalse();
        live.Meta.ThinkingLevel.Should().Be("medium");
        live.Meta.HostEnabled.Should().BeTrue();
        live.Meta.Revision.Should().Be(5);
        live.Model.Should().Be("new-model");
    }

    [Fact]
    public void Merge_HigherRevisionLive_KeepsLiveSettings()
    {
        var live = new SessionState();
        live.Meta.ThinkingLevel = "xhigh";
        live.Meta.Revision = 9;

        var incoming = new SessionState();
        incoming.Meta.ThinkingLevel = "off";
        incoming.Meta.Revision = 3;

        var changed = SessionMetaMerger.Merge(live, incoming);

        changed.Should().BeFalse();
        live.Meta.ThinkingLevel.Should().Be("xhigh");
        live.Meta.Revision.Should().Be(9);
    }

    [Fact]
    public void Merge_EqualRevisions_IncomingAuthoritative()
    {
        // Legacy files predate revisions (all zero); equal revisions means the
        // disk copy is the resume source of truth.
        var live = new SessionState { Meta = { ThinkingLevel = "off" } };
        var incoming = new SessionState { Meta = { ThinkingLevel = "low" } };

        SessionMetaMerger.Merge(live, incoming);

        live.Meta.ThinkingLevel.Should().Be("low");
    }

    [Fact]
    public void Merge_TodosFollowHigherRevision()
    {
        var live = new SessionState();
        live.Todos.Add(new TodoItem { Content = "live todo", Status = "pending" });
        live.Meta.Revision = 2;

        var incoming = new SessionState();
        incoming.Todos.Add(new TodoItem { Content = "incoming todo", Status = "in_progress" });
        incoming.Meta.Revision = 7;

        SessionMetaMerger.Merge(live, incoming);

        live.Todos.Should().ContainSingle().Which.Content.Should().Be("incoming todo");
    }

    [Fact]
    public void Merge_LiveHigherRevision_KeepsItsTodos()
    {
        var live = new SessionState();
        live.Todos.Add(new TodoItem { Content = "live todo", Status = "pending" });
        live.Meta.Revision = 8;

        var incoming = new SessionState();
        incoming.Todos.Add(new TodoItem { Content = "incoming todo", Status = "pending" });
        incoming.Meta.Revision = 4;

        SessionMetaMerger.Merge(live, incoming);

        live.Todos.Should().ContainSingle().Which.Content.Should().Be("live todo");
    }
}
