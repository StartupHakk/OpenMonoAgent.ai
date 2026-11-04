// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text.Json.Nodes;
using Omabot.Api;
using Omabot.Approvals;
using Omabot.Common;
using Omabot.Containers;
using Omabot.Db;
using Omabot.Gateway;
using Omabot.Routines;
using Omabot.Runtime;
using Omabot.Supervision;
using Omabot.Tests.Support;

namespace Omabot.SecurityTests;

public sealed class Phase4SecurityTests : IDisposable
{
    private readonly TempRoster _r = new();
    public void Dispose() => _r.Dispose();

    private (int Code, string Err) Hook(string tool, string allowed, string? gate)
    {
        var hook = Path.Combine(Applied.RepoRoot(), "bot", "hooks", "pre-tool.sh");
        var list = Path.Combine(_r.Root, "allowed.txt");
        File.WriteAllText(list, allowed);
        var psi = new ProcessStartInfo("bash") { RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add(hook);
        psi.ArgumentList.Add(tool);
        psi.Environment["ROSTER_ALLOWED_TOOLS"] = list;
        var gateFile = Path.Combine(_r.Root, "turn-gate");
        if (gate is not null) File.WriteAllText(gateFile, gate);
        else if (File.Exists(gateFile)) File.Delete(gateFile);
        psi.Environment["ROSTER_TURN_GATE"] = gateFile;
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, err);
    }

    [Fact(DisplayName = "Routine turn gate: the hook blocks changing tools on an edits bot, allows reads, and is gone after the turn")]
    public async Task RoutineGate()
    {
        var db = await Applied.DefaultPlusTwoAsync(_r);
        var builder = new BotStore(db, _r.Clock).Get("Builder").Spec;
        var blocks = TurnGate.RoutineBlocks(builder);
        Assert.Contains("FileWrite", blocks);
        Assert.Contains("Bash", blocks);
        Assert.DoesNotContain("FileRead", blocks);
        Assert.Empty(TurnGate.RoutineBlocks(new BotStore(db, _r.Clock).Get("Scout").Spec)); // ask bot: OMA pauses, the broker decides
        TurnGate.Write(_r.Paths, builder.Slug, "routine", blocks);
        var gate = File.ReadAllText(TurnGate.PathFor(_r.Paths, builder.Slug));
        var allowed = "FileRead\nFileWrite\nBash\n";
        var write = Hook("FileWrite", allowed, gate);
        Assert.Equal(2, write.Code);
        Assert.Contains("not allowed in a routine turn", write.Err, StringComparison.Ordinal);
        Assert.Equal(0, Hook("FileRead", allowed, gate).Code);
        TurnGate.Reset(_r.Paths, builder.Slug);
        Assert.Equal(0, Hook("FileWrite", allowed, File.ReadAllText(TurnGate.PathFor(_r.Paths, builder.Slug))).Code);
        Assert.Equal(0, Hook("FileWrite", allowed, null).Code);
        // The tool list still fails closed with or without a gate.
        Assert.Equal(2, Hook("HostExec", allowed, null).Code);
        // A gate line cannot be injected through the origin label or tool names.
        Assert.Throws<ArgumentException>(() => TurnGate.Write(_r.Paths, builder.Slug, "routine\nblocked=FileRead", []));
        TurnGate.Write(_r.Paths, builder.Slug, "routine", ["FileWrite\nblocked=FileRead"]);
        Assert.DoesNotContain("blocked=FileRead", File.ReadAllText(TurnGate.PathFor(_r.Paths, builder.Slug)), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Turn lease: one roster turn per bot, only the owner releases it, an expired lease can be taken")]
    public void TurnLeases()
    {
        var db = _r.Db();
        var turns = new TurnContexts(db, _r.Clock, Priority.Assignment);
        var a = turns.TryAcquire("scout", Priority.Routine, "r", TimeSpan.FromMinutes(5));
        Assert.NotNull(a);
        Assert.Null(turns.TryAcquire("scout", Priority.Interactive, null, TimeSpan.FromMinutes(5)));
        Assert.NotNull(turns.TryAcquire("builder", Priority.Interactive, null, TimeSpan.FromMinutes(5)));
        turns.Release(a with { Owner = "turn_forged" });
        Assert.True(turns.Busy("scout"));
        Assert.Equal(Priority.Routine, turns.For("scout").Priority);
        _r.Clock.Advance(TimeSpan.FromMinutes(6));
        var b = turns.TryAcquire("scout", Priority.Interactive, null, TimeSpan.FromMinutes(5));
        Assert.NotNull(b);
        Assert.False(turns.Holds(a));
        turns.Release(a);
        Assert.True(turns.Holds(b));
        turns.Release(b);
        Assert.False(turns.Busy("scout"));
    }

    [Fact(DisplayName = "SEC-21 a bot cannot call a roster tool that is not on its allow list, even with a valid turn token")]
    public async Task ShimAllowList()
    {
        var db = await Applied.DefaultPlusTwoAsync(_r);
        var bots = new BotStore(db, _r.Clock);
        var host = new RosterToolHost((bot, tool) => RosterToolHost.BotMayUse(bots.Find(bot)?.Spec, tool));
        var approvals = new ApprovalStore(db, _r.Clock, Redactor.Shared);
        RoutineTools.Register(host, db, _r.Config, _r.Clock, new RoutineChanges(db, _r.Config, _r.Clock, _r.Audit(db), approvals));
        // The health bot is read only: routine_create is not served to it at all.
        Assert.DoesNotContain(host.ListJson("health"), t => t!["name"]!.GetValue<string>() == "routine_create");
        var r = await host.CallAsync(new ToolCaller("health", null, null), "routine_create",
            new JsonObject { ["name"] = "x", ["interval_minutes"] = 30, ["instructions"] = "x" }, CancellationToken.None);
        Assert.Equal(404, r.Status);
        Assert.Empty(approvals.Pending());
        // An unknown bot gets nothing.
        Assert.Empty(host.For("nobody"));
        Assert.False(RosterToolHost.BotMayUse(null, "list_bots"));
        // memory_read comes only with a memory tool.
        Assert.True(RosterToolHost.BotMayUse(bots.Get("head-honcho").Spec, "memory_read"));
        Assert.False(RosterToolHost.BotMayUse(bots.Get("health").Spec, "memory_read"));
    }

    [Fact(DisplayName = "Check containers: the supervisor refuses unsafe specs (docker.sock, writable checks, foreign mounts, network without the gate)")]
    public async Task CheckContainerSafety()
    {
        var checksDir = Path.Combine(_r.AppDir, "checks");
        Directory.CreateDirectory(checksDir);
        var ws = Path.Combine(_r.WorkRoot, "ws");
        Directory.CreateDirectory(ws);
        var engine = new FakeContainerEngine();
        var sup = new DirectSupervisor(engine, "oma-roster", [checksDir, _r.WorkRoot]);
        var good = CheckContainerSpec.For("scout", "a.sh", ["x"], checksDir, ws, false, "none", null, _r.Config, TimeSpan.FromSeconds(10));
        Assert.Empty(good.SafetyProblems([checksDir, _r.WorkRoot], "oma-roster"));
        var bad = new[]
        {
            good with { Mounts = [.. good.Mounts, new Mount("/var/run/docker.sock", "/workspace/d", false)] },
            good with { Mounts = [new Mount(checksDir, CheckContainerSpec.ChecksMount, false), good.Mounts[1]] },
            good with { Mounts = [good.Mounts[0], new Mount(_r.Paths.Secrets, CheckContainerSpec.WorkspaceMount, true)] },
            good with { Network = "oma-roster" },
            good with { Network = "host" },
            good with { Entrypoint = ["/bin/sh", "-c", "id"] },
            good with { Command = ["$(id)"] },
            good with { User = "0:0" },
            good with { Name = "openmono-agent" },
        };
        foreach (var spec in bad)
            await Assert.ThrowsAsync<ContainerEngineException>(() => sup.RunCheckAsync(spec));
        Assert.Empty(engine.RawCreated);
        // A supervisor without allowed roots refuses every check container.
        await Assert.ThrowsAsync<ContainerEngineException>(() => new DirectSupervisor(engine, "oma-roster").RunCheckAsync(good));
    }
}
