// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Omabot.Api;
using Omabot.Common;
using Omabot.Db;
using Omabot.Memory;
using Omabot.McpShim;
using Omabot.Tests.Support;
using Xunit;

namespace Omabot.Tests;

public sealed class MemoryTests : IDisposable
{
    private readonly TempRoster _r = new();
    private readonly RosterDb _db;
    private readonly string _ws;
    private static readonly DateOnly Today = new(2026, 11, 3);

    public MemoryTests()
    {
        _db = _r.Db();
        _ws = Path.Combine(_r.WorkRoot, "scout");
        Directory.CreateDirectory(Path.Combine(_ws, "memory"));
    }

    public void Dispose() => _r.Dispose();

    private MemoryStore Store(MemoryLimits? limits = null)
        => new(_db, _r.Clock, Redactor.Shared, "scout", _ws, Path.Combine(_r.Paths.Home, "bots", "scout", "journal"), limits ?? new MemoryLimits(), "America/Phoenix");

    private static string Entry(string date, string fact, string? extra = null) => $"- {date} \u00b7 from chat \u00b7 {fact}{extra}";

    [Fact(DisplayName = "UNIT-MEM-01 load cuts at 200 lines")]
    public void LoadCutsAtLines()
    {
        var text = string.Join('\n', Enumerable.Range(1, 260).Select(i => Entry("2026-11-01", "fact " + i))) + "\n";
        var l = MemoryBudget.Load(text, Today, new MemoryLimits());
        Assert.True(l.Cut);
        Assert.Equal(200, l.LinesLoaded);
        Assert.Contains("fact 200", l.Block);
        Assert.DoesNotContain("fact 201", l.Block);
        Assert.Contains("was cut", l.Block);
    }

    [Fact(DisplayName = "UNIT-MEM-02 load cuts at 24 KB when that comes first")]
    public void LoadCutsAtBytes()
    {
        var big = new string('x', 900);
        var text = string.Join('\n', Enumerable.Range(1, 50).Select(i => Entry("2026-11-01", i + big))) + "\n";
        var l = MemoryBudget.Load(text, Today, new MemoryLimits());
        Assert.True(l.Cut);
        Assert.True(l.LinesLoaded < 50);
        Assert.True(Encoding.UTF8.GetByteCount(l.Block.Replace(MemoryBudget.CutNotice, "")) <= 24 * 1024);
    }

    [Fact(DisplayName = "UNIT-MEM-03 archive move order: struck, expired, oldest live, each marked moved")]
    public void MoveOrder()
    {
        var text = string.Join('\n',
            Entry("2026-01-01", "oldest live"),
            Entry("2026-06-01", "expired", " \u00b7 until 2026-10-01"),
            "- ~~2026-10-30 \u00b7 from chat \u00b7 struck one~~",
            Entry("2026-10-31", "newer live")) + "\n";
        var limits = new MemoryLimits(MaxLines: 1);
        var fit = MemoryBudget.Fit(text, Today, limits);
        Assert.True(fit.Fits);
        Assert.Equal(3, fit.Moved.Count);
        Assert.Contains("struck one", fit.Moved[0]);
        Assert.Contains("expired", fit.Moved[1]);
        Assert.Contains("oldest live", fit.Moved[2]);
        Assert.All(fit.Moved, m => Assert.Contains("\u00b7 moved 2026-11-03", m));
        Assert.Contains("newer live", fit.Text);
    }

    [Fact(DisplayName = "UNIT-MEM-04 undated lines and health facts never move")]
    public void NeverMove()
    {
        var text = string.Join('\n', "Spencer wrote this by hand", Entry("2026-01-01", "Safety: never run rm on /srv"), Entry("2026-02-01", "movable")) + "\n";
        var fit = MemoryBudget.Fit(text, Today, new MemoryLimits(MaxLines: 1));
        Assert.False(fit.Fits);
        Assert.Single(fit.Moved);
        Assert.Contains("Spencer wrote this by hand", fit.Text);
        Assert.Contains("Safety: never run rm", fit.Text);
    }

    [Theory(DisplayName = "UNIT-MEM-05 redaction pattern classes")]
    [InlineData("key sk-proj-abcdefghijklmnop1234567890")]
    [InlineData("slack xoxb-1234567890-abcdefghij")]
    [InlineData("gh ghp_abcdefghijklmnopqrstuvwxyz0123")]
    [InlineData("gl glpat-abcdefghijklmnopqrst")]
    [InlineData("aws AKIAABCDEFGHIJKLMNOP")]
    [InlineData("Authorization: Bearer abcdefghijklmnop.qrstu")]
    [InlineData("password: hunter2hunter2")]
    [InlineData("db postgres://app:s3cretpass@db.local/x")]
    [InlineData("signature: 9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIabc\n-----END RSA PRIVATE KEY-----")]
    public void RedactsOnWrite(string secretLine)
    {
        var s = Store();
        var r = s.AddEntry("note " + secretLine, "chat", null, null, "absent", "bot tool", "test");
        Assert.True(r.Ok, r.Message);
        var disk = File.ReadAllText(Path.Combine(_ws, "MEMORY.md"));
        Assert.Contains("\u00abredacted", disk);
        Assert.DoesNotContain("hunter2hunter2", disk);
        Assert.DoesNotContain("s3cretpass", disk);
        Assert.DoesNotContain("abcdefghijklmnop1234567890", disk);
        Assert.DoesNotContain("MIIabc", disk);
    }

    [Fact(DisplayName = "UNIT-MEM-05 known roster secret is always replaced")]
    public void KnownSecret()
    {
        Redactor.Shared.AddKnownSecret("plainwordsecretvalue42");
        var r = Store().AddEntry("the pair secret is plainwordsecretvalue42", "chat", null, null, "absent", "bot tool", "t");
        Assert.True(r.Ok);
        Assert.DoesNotContain("plainwordsecretvalue42", File.ReadAllText(Path.Combine(_ws, "MEMORY.md")));
    }

    [Fact(DisplayName = "UNIT-MEM-06 simulated crash leaves the old file whole")]
    public void AtomicWrite()
    {
        var s = Store();
        Assert.True(s.AddEntry("first", "chat", null, null, "absent", "bot tool", "t").Ok);
        var before = File.ReadAllText(Path.Combine(_ws, "MEMORY.md"));
        SecureFile.BeforeRenameForTests = tmp => { if (tmp.StartsWith(_ws, StringComparison.Ordinal)) throw new IOException("simulated crash"); };
        try
        {
            Assert.Throws<IOException>(() => s.AddEntry("second", "chat", null, null, s.Read("MEMORY.md").Rev, "bot tool", "t"));
        }
        finally
        {
            SecureFile.BeforeRenameForTests = null;
        }
        Assert.Equal(before, File.ReadAllText(Path.Combine(_ws, "MEMORY.md")));
        Assert.Empty(Directory.GetFiles(_ws, "*.tmp"));
    }

    [Fact(DisplayName = "UNIT-MEM-07 undo restores, refuses when the file changed since")]
    public void Undo()
    {
        var s = Store();
        var a = s.AddEntry("alpha", "chat", null, null, "absent", "bot tool", "t");
        var b = s.AddEntry("beta", "chat", null, null, a.Rev, "bot tool", "t");
        var refused = s.Undo(a.JournalId!.Value, "person");
        Assert.Equal(MemoryStatus.Conflict, refused.Status);
        var ok = s.Undo(b.JournalId!.Value, "person");
        Assert.True(ok.Ok, ok.Message);
        var text = s.Read("MEMORY.md").Text!;
        Assert.Contains("alpha", text);
        Assert.DoesNotContain("beta", text);
        Assert.True(s.Undo(a.JournalId!.Value, "person").Ok);
        Assert.Equal("", s.Read("MEMORY.md").Text ?? "");
        Assert.True(Directory.GetFiles(Path.Combine(_r.Paths.Home, "bots", "scout", "journal"), "memory-*.json").Length >= 4);
    }

    [Fact(DisplayName = "UNIT-MEM-08 entry with until yesterday does not load")]
    public void Expiry()
    {
        var text = Entry("2026-10-01", "gone", " \u00b7 until 2026-11-02") + "\n" + Entry("2026-10-01", "today ok", " \u00b7 until 2026-11-03") + "\n";
        var l = MemoryBudget.Load(text, Today, new MemoryLimits());
        Assert.DoesNotContain("gone", l.Block);
        Assert.Contains("today ok", l.Block);
        Assert.Equal(1, l.ExpiredSkipped);
    }

    [Fact(DisplayName = "UNIT-MEM-09 exact duplicate ignores spacing, bullet and final period only")]
    public void Duplicates()
    {
        Assert.True(MemoryEntries.SameFact("- Spencer  likes tea.", "Spencer likes tea"));
        Assert.False(MemoryEntries.SameFact("Spencer likes tea", "spencer likes tea"));
        Assert.False(MemoryEntries.SameFact("Spencer likes tea", "Spencer likes teas"));
        var text = Entry("2026-10-01", "Spencer likes tea.") + "\n" + Entry("2026-10-05", "Spencer  likes tea") + "\n" + Entry("2026-10-06", "Spencer likes Tea") + "\n";
        var (t, _, dupes) = MemoryUpkeep.TidyText(text, Today);
        Assert.Equal(1, dupes);
        Assert.Contains("2026-10-05", t);
        Assert.DoesNotContain("2026-10-01", t);
        Assert.Contains("likes Tea", t);
    }

    [Fact(DisplayName = "Memory write needs a revision and refuses a stale one")]
    public void RevisionChecks()
    {
        var s = Store();
        Assert.Equal(428, s.AddEntry("x", "chat", null, null, null, "bot tool", "t").HttpStatus);
        Assert.True(s.AddEntry("x", "chat", null, null, "absent", "bot tool", "t").Ok);
        Assert.Equal(409, s.AddEntry("y", "chat", null, null, "absent", "bot tool", "t").HttpStatus);
    }

    [Fact(DisplayName = "Tool write keeps MEMORY.md inside the budget and reports moves")]
    public void BudgetOnWrite()
    {
        var s = Store(new MemoryLimits(MaxLines: 10));
        var rev = "absent";
        for (var i = 0; i < 25; i++)
        {
            var r = s.AddEntry("fact number " + i, "chat", null, null, rev, "bot tool", "t");
            Assert.True(r.Ok, r.Message);
            rev = r.Rev!;
            Assert.True(MemoryEntries.LineCount(s.Read("MEMORY.md").Text!) <= 10);
        }
        var arch = s.Read(MemoryStore.ArchiveFile).Text!;
        Assert.Contains("fact number 0 \u00b7 moved", arch);
    }

    [Fact(DisplayName = "Entry limits: 1,000 characters and 20 lines")]
    public void EntryLimits()
    {
        var s = Store();
        Assert.Equal(MemoryStatus.TooLong, s.AddEntry(new string('a', 1001), "chat", null, null, "absent", "bot tool", "t").Status);
        Assert.Equal(MemoryStatus.TooLong, s.AddEntry(string.Join('\n', Enumerable.Range(0, 21).Select(i => "l" + i)), "chat", null, null, "absent", "bot tool", "t").Status);
    }

    [Fact(DisplayName = "Memory never follows a symlink planted by the bot")]
    public void NoSymlinks()
    {
        var secret = Path.Combine(_r.Root, "secret.txt");
        File.WriteAllText(secret, "outside");
        File.CreateSymbolicLink(Path.Combine(_ws, "MEMORY.md"), secret);
        var s = Store();
        Assert.True(s.Read("MEMORY.md").Unsafe);
        Assert.Equal(MemoryStatus.Unsafe, s.AddEntry("x", "chat", null, null, "absent", "bot tool", "t").Status);
        Assert.Equal("outside", File.ReadAllText(secret));
        Assert.Equal("(no memory yet)", s.Load().Block);
    }

    [Fact(DisplayName = "Direct write with a secret is redacted and journaled without undo")]
    public void DirectWriteScan()
    {
        File.WriteAllText(Path.Combine(_ws, "MEMORY.md"), "token: abcdefghijklmnopqrstuv123\n");
        var s = Store();
        Assert.Equal(["MEMORY.md"], s.ScanDirectWrites());
        Assert.DoesNotContain("abcdefghijklmnopqrstuv123", File.ReadAllText(Path.Combine(_ws, "MEMORY.md")));
        var row = s.JournalRows(1)[0];
        Assert.Equal("redacted after direct write", row.Reason);
        Assert.False(row.Undoable);
        Assert.DoesNotContain("abcdefghijklmnopqrstuv123", (string?)_db.Scalar("SELECT COALESCE(before_text,'') FROM memory_journal;"));
    }

    [Fact(DisplayName = "Forget removes and blocks capture from adding it back")]
    public void Forget()
    {
        var s = Store();
        var r = s.AddEntry("Spencer likes jazz", "chat", null, null, "absent", "bot tool", "t");
        Assert.True(s.Forget("Spencer likes jazz.", null, r.Rev, "bot tool").Ok);
        Assert.DoesNotContain("jazz", s.Read("MEMORY.md").Text);
        Assert.True(s.IsForgotten("Spencer likes jazz"));
    }

    [Fact(DisplayName = "Recall puts at most 4 notes in front of the message, never for short messages")]
    public void RecallNotes()
    {
        var s = Store();
        File.WriteAllText(Path.Combine(_ws, "memory", "projects.md"), "---\ntitle: Projects\n---\n" + string.Join('\n', Enumerable.Range(1, 8).Select(i => Entry("2026-10-01", $"sandbox checkout note {i}"))) + "\n");
        var msg = Recall.Apply(s, "where is the sandbox checkout?");
        Assert.StartsWith(Recall.Header, msg);
        Assert.Equal(4, msg.Split('\n').Count(l => l.StartsWith("(projects)", StringComparison.Ordinal)));
        Assert.EndsWith("where is the sandbox checkout?", msg);
        Assert.Equal("hi", Recall.Apply(s, "hi"));
    }

    private sealed class FakeModel(params string[] replies) : IUpkeepModel
    {
        private int _i;
        public List<string> Users { get; } = [];
        public Task<string?> CompleteAsync(string bot, string system, string user, CancellationToken ct)
        {
            Users.Add(user);
            return Task.FromResult<string?>(_i < replies.Length ? replies[_i++] : null);
        }
    }

    [Fact(DisplayName = "Capture keeps only quote-backed facts, never secrets")]
    public async Task Capture()
    {
        var s = Store();
        var model = new FakeModel("""{"facts":[{"fact":"Spencer wants notices only on errors","quote":"only tell me on errors"},{"fact":"Spencer loves golf","quote":"I love golf"},{"fact":"The db password is hunter2hunter2","quote":"password: hunter2hunter2"}]}""");
        var up = new MemoryUpkeep(s, _db, _r.Clock, model, Path.Combine(_r.Root, "turns.jsonl"));
        up.RecordPersonTurn("Please only tell me on errors. Also password: hunter2hunter2", "OK", true);
        Assert.False(up.CaptureDue());
        _r.Clock.Advance(TimeSpan.FromMinutes(3));
        Assert.True(up.CaptureDue());
        var rep = await up.CaptureAsync(default);
        Assert.Equal(1, rep.Changed);
        var text = s.Read("MEMORY.md").Text!;
        Assert.Contains("notices only on errors", text);
        Assert.DoesNotContain("golf", text);
        Assert.DoesNotContain("hunter2hunter2", text);
        Assert.DoesNotContain("hunter2hunter2", File.ReadAllText(Path.Combine(_r.Root, "turns.jsonl")));
        Assert.DoesNotContain("hunter2hunter2", string.Join("", model.Users));
        Assert.False(up.CaptureDue());
    }

    [Fact(DisplayName = "Tidy strikes at most a fifth, none under 5 entries")]
    public async Task TidyCap()
    {
        Assert.Equal(0, MemoryUpkeep.StrikeCap(4));
        Assert.Equal(1, MemoryUpkeep.StrikeCap(9));
        var s = Store();
        File.WriteAllText(Path.Combine(_ws, "MEMORY.md"), string.Join('\n', Enumerable.Range(1, 6).Select(i => Entry($"2026-10-0{i}", "fact " + i))) + "\n");
        var up = new MemoryUpkeep(s, _db, _r.Clock, new FakeModel("""{"pairs":[[1,2],[3,4]]}"""), Path.Combine(_r.Root, "t.jsonl"));
        var rep = await up.TidyAsync(default);
        Assert.Equal(1, rep.Changed);
        Assert.Contains("~~2026-10-01", s.Read("MEMORY.md").Text);
        Assert.False(up.TidyDue());
    }

    [Fact(DisplayName = "Shim: tools list, turn token, auto revision and stale token")]
    public async Task ShimRoundTrip()
    {
        var s = Store();
        var tokens = new ShimTokens(_db, _r.Clock);
        var host = new RosterToolHost(_ => []);
        MemoryTools.Register(host, _ => s);
        var server = new ShimServer(host, tokens);
        using var http = new HttpClient(new InProcess(server, "scout")) { BaseAddress = new Uri("http://roster") };
        var shim = new ShimClient(http);

        var list = await shim.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        Assert.Contains("memory_update", list);

        var none = await shim.CallToolAsync("memory_update", new JsonObject { ["fact"] = "x" });
        Assert.False(none.Ok); // no turn running

        var contexts = new Gateway.TurnContexts(_db, _r.Clock, Gateway.Priority.Assignment);
        contexts.Set("scout", Gateway.Priority.Interactive, null, TimeSpan.FromMinutes(5));
        var ok = await shim.CallToolAsync("memory_update", new JsonObject { ["fact"] = "Spencer drinks tea" });
        Assert.True(ok.Ok, ok.Text);
        Assert.Contains("Spencer drinks tea", s.Read("MEMORY.md").Text);

        // A new turn invalidates the old token; the shim claims again once.
        contexts.Clear("scout");
        _r.Clock.Advance(TimeSpan.FromSeconds(2));
        contexts.Set("scout", Gateway.Priority.Interactive, null, TimeSpan.FromMinutes(5));
        var again = await shim.CallToolAsync("memory_update", new JsonObject { ["fact"] = "second fact" });
        Assert.True(again.Ok, again.Text);
    }

    [Fact(DisplayName = "Shim token from one bot does not work for another")]
    public void TokenBoundToBot()
    {
        var contexts = new Gateway.TurnContexts(_db, _r.Clock, Gateway.Priority.Assignment);
        contexts.Set("scout", Gateway.Priority.Interactive, null, TimeSpan.FromMinutes(5));
        contexts.Set("other", Gateway.Priority.Interactive, null, TimeSpan.FromMinutes(5));
        var tokens = new ShimTokens(_db, _r.Clock);
        var t = tokens.Claim("scout")!;
        Assert.True(tokens.Check("scout", t, out _));
        Assert.False(tokens.Check("other", t, out _));
    }

    [Fact(Skip = "SCN-02 needs Docker and a live gateway: fact recalled after oma-bot restart. Run on a host with Docker.", DisplayName = "SCN-02 memory across restart (live)")]
    public void Scn02Live() { }

    [Fact(Skip = "INT-04 needs Docker: the shim inside a container lists roster tools and calls list_bots.", DisplayName = "INT-04 MCP shim inside a container (live)")]
    public void Int04Live() { }

    /// <summary>Runs the shim server's handler in process, standing in for the unix socket.</summary>
    internal sealed class InProcess(ShimServer server, string bot) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Method = request.Method.Method;
            ctx.Request.Path = request.RequestUri!.AbsolutePath;
            if (request.Headers.Authorization is { } a) ctx.Request.Headers.Authorization = a.ToString();
            ctx.Request.Body = request.Content is null ? new MemoryStream() : new MemoryStream(await request.Content.ReadAsByteArrayAsync(ct));
            var body = new MemoryStream();
            ctx.Response.Body = body;
            await server.HandleAsync(ctx, bot);
            return new HttpResponseMessage((System.Net.HttpStatusCode)ctx.Response.StatusCode) { Content = new ByteArrayContent(body.ToArray()) };
        }
    }
}
