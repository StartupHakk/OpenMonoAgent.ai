// SPDX-License-Identifier: AGPL-3.0-or-later
using Omabot.Acp;
using Omabot.Api;
using Omabot.Cli;
using Omabot.Common;
using Omabot.Db;
using Omabot.Gateway;
using Omabot.Runtime;
using Omabot.Supervision;

namespace Omabot.Tests.Support;

/// <summary>
/// A roster with several bots, each backed by its own fake ACP server, and
/// the handoff runtime and tools wired the way `oma-bot serve` wires them.
/// A bot's behavior is a function of the message it gets; inside it a test
/// can call roster tools as that bot during its turn.
/// </summary>
public sealed class Roster : IAsyncDisposable
{
    public const string Team = """
        version: 1
        models:
          flash-next:
            upstream_model: Qwen3.8-Flash-Next
        teams:
          - { name: Core, lead: Lead }
        routes:
          - { words: [test, build, compile], to: Builder }
          - { words: [docs, wording], to: Editor }
        bots:
          - name: Lead
            role: lead
            team: Core
            personality: You are Lead.
            model: flash-next
            tools: { allow: [FileRead, mcp__roster__assign, mcp__roster__consult, mcp__roster__notify, mcp__roster__assignment_status, mcp__roster__ask, mcp__roster__answer, mcp__roster__cancel, mcp__roster__report] }
            accepts_from: [person]
          - name: Scout
            team: Core
            personality: You are Scout.
            model: flash-next
            tools: { allow: [FileRead, Grep, mcp__roster__assign, mcp__roster__consult, mcp__roster__notify, mcp__roster__ask, mcp__roster__answer, mcp__roster__assignment_status] }
            accepts_from: [person, Lead, Builder, Editor]
          - name: Builder
            team: Core
            personality: You are Builder.
            model: flash-next
            permission_profile: edits
            tools: { allow: [FileRead, FileWrite, FileEdit, Bash, mcp__roster__assign, mcp__roster__consult, mcp__roster__notify, mcp__roster__ask, mcp__roster__answer, mcp__roster__assignment_status] }
            accepts_from: [person, Lead, Scout]
          - name: Editor
            team: Core
            personality: You are Editor.
            model: flash-next
            permission_profile: edits
            tools: { allow: [FileRead, FileEdit, mcp__roster__assign, mcp__roster__ask, mcp__roster__assignment_status] }
            accepts_from: [person, Lead, Scout, Builder]
          - name: Helper
            team: Core
            personality: You are Helper.
            model: flash-next
            tools: { allow: [FileRead] }
            accepts_from: [person, Lead, Scout, Builder, Editor]
        """;

    public TempRoster R { get; }
    public RosterDb Db { get; }
    public BotStore Bots { get; }
    public AssignmentRuntime Runtime { get; private set; }
    public RosterToolHost Tools { get; private set; }
    public Notices Notices { get; }
    public Dictionary<string, FakeAcpServer> Servers { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Func<string, IEnumerable<(string Event, object Data)>>> Behavior { get; } = new(StringComparer.Ordinal);
    private readonly Dictionary<int, AcpClient> _clients = [];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    public Func<string, CancellationToken, Task<string?>>? EnsureRunning { get; set; }

    private Roster(TempRoster r, RosterDb db, bool broker)
    {
        R = r;
        Db = db;
        Bots = new BotStore(db, r.Clock);
        Notices = new Notices(db, r.Clock, Redactor.Shared);
        WithBroker = broker;
        (Runtime, Tools) = Build();
    }

    public static async Task<Roster> StartAsync(TempRoster r, string? team = null, bool broker = false)
    {
        var env = new CliEnv { Paths = r.Paths, AppDir = r.AppDir, Clock = r.Clock, Out = new StringWriter(), Err = new StringWriter() };
        var code = await new CliApp(env).RunAsync(["team", "apply", r.TeamFile(team ?? Team)]);
        if (code != 0) throw new InvalidOperationException(env.Err.ToString());
        var roster = new Roster(r, r.Db(), broker);
        foreach (var bot in roster.Bots.All())
        {
            var srv = await FakeAcpServer.StartAsync();
            var slug = bot.Slug;
            roster.Servers[slug] = srv;
            roster._clients[bot.Port] = new AcpClient(Http, srv.BaseUrl);
            roster.Bots.SetState(slug, BotStates.Running);
            srv.OnMessage = (_, m) => roster.Behavior.TryGetValue(slug, out var b) ? b(m) : [("text_delta", new { content = $"{slug} done" }), ("done", new { })];
        }
        return roster;
    }

    /// <summary>When true, pauses go to the approval broker (as in `oma-bot serve`), with approval_answer and the bot tools registered.</summary>
    public bool WithBroker { get; init; }
    public Approvals.ApprovalBroker? Broker { get; private set; }
    public BotChanges? BotChanges { get; private set; }

    /// <summary>A new runtime on the same database, as after a service restart.</summary>
    public (AssignmentRuntime Runtime, RosterToolHost Tools) Build()
    {
        var driver = new TurnDriver(Db, R.Clock, R.Paths, port => _clients[port], (name, ct) => EnsureRunning?.Invoke(name, ct) ?? Task.FromResult<string?>($"{name} is not running"));
        var audit = R.Audit(Db);
        var runtime = new AssignmentRuntime(Db, R.Config, R.Paths, R.Clock, audit, Notices, driver);
        var tools = new RosterToolHost((_, _) => true);
        HandoffTools.Register(tools, runtime, Db, R.Clock, Notices);
        if (WithBroker)
        {
            Broker = new Approvals.ApprovalBroker(runtime);
            runtime.Pauses = Broker;
            Broker.Register(tools);
            BotChanges = new BotChanges(Db, R.Config, R.Paths, R.Clock, audit, Notices, runtime.Approvals);
            BotTools.Register(tools, runtime, BotChanges, Db, R.Config, R.Paths, R.Clock, audit);
        }
        return (runtime, tools);
    }

    /// <summary>One broker tick, then waits for any turn it started.</summary>
    public async Task<IReadOnlyList<string>> BrokerTickAsync()
    {
        var lines = await Broker!.TickAsync(CancellationToken.None);
        await Runtime.WhenIdleAsync();
        return lines;
    }

    /// <summary>A person's answer from the CLI identity, through the full policy.</summary>
    public Approvals.DecideResult PersonAnswer(string id, bool allow, string? text = null, string? hash = null, string? reason = null)
        => Runtime.Approvals.Decide(id, Approvals.Approver.Person("spencer"), allow, reason, hash, Broker?.Policy ?? Approvals.PersonOnlyPolicy.Instance, text);

    public void Restart() => (Runtime, Tools) = Build();

    /// <summary>The assignment the bot's current roster turn works on (what the shim would report).</summary>
    public string? CurrentAssignment(string slug) => Db.Scalar("SELECT assignment FROM turn_context WHERE bot=$b;", ("$b", slug)) as string;

    /// <summary>Calls a roster tool as the bot, during its turn (synchronously, as a fake bot behavior needs).</summary>
    public ToolResult Call(string slug, string tool, System.Text.Json.Nodes.JsonObject args)
        => Tools.CallAsync(new ToolCaller(slug, null, CurrentAssignment(slug)), tool, args, CancellationToken.None).GetAwaiter().GetResult();

    public static IEnumerable<(string Event, object Data)> Say(string text) => [("text_delta", new { content = text }), ("done", new { })];

    /// <summary>Starts a person's message to a bot as a root assignment and runs its first turn.</summary>
    public async Task<AssignmentRow> PersonAsync(string bot, string message, string? mode = null)
    {
        var lease = Runtime.Turns.TryAcquire(bot, Priority.Interactive, null, TimeSpan.FromMinutes(30)) ?? throw new InvalidOperationException($"{bot} is busy");
        try
        {
            var e = Runtime.Enqueue(new EnqueueRequest(bot, "work", message, Rework: true, Origin: "person", RequesterKind: "person", Session: "standing",
                Priority: Priority.Interactive, Actor: "person:test", ClaimWith: lease, TurnMessage: message));
            if (!e.Ok) throw new InvalidOperationException(e.Message);
            var (_, row) = await Runtime.RunNowAsync(e.Id!, lease, mode, null, CancellationToken.None);
            return row;
        }
        finally
        {
            Runtime.Turns.Release(lease);
        }
    }

    /// <summary>Ticks the runtime until nothing is left to admit and nothing runs.</summary>
    public async Task DrainAsync(int maxTicks = 30)
    {
        for (var i = 0; i < maxTicks; i++)
        {
            var lines = await Runtime.TickAsync(CancellationToken.None);
            await Runtime.WhenIdleAsync();
            var ready = Runtime.Store.ByStatus(AssignmentStatus.Queued, AssignmentStatus.Resume).Count(a => a.JoinedTo is null);
            if (lines.Count == 0 && ready == 0) return;
        }
    }

    public void AssertInvariants(bool quiesced = true)
    {
        var broken = Invariants.Check(Db, quiesced);
        Assert.True(broken.Count == 0, "Invariants broken:\n" + string.Join("\n", broken));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in Servers.Values) await s.DisposeAsync();
    }
}
