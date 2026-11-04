// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Omabot.Cli;
using Omabot.Common;
using Omabot.Containers;
using Omabot.Db;
using Omabot.Gateway;
using Omabot.Render;
using Omabot.Tests.Support;

namespace Omabot.Tests;

/// <summary>Phase 8: tool-call repair in the gateway, request shaping, sampling presets and the tool task set.</summary>
public sealed class ToolCallTests : IDisposable
{
    private readonly TempRoster _r = new();
    public void Dispose() => _r.Dispose();

    private static readonly Dictionary<string, JsonObject?> Offered = ToolCallRepair.OfferedTools(new JsonObject { ["tools"] = ToolBench.Tools() });

    private static AssembledReply Reply(string? content, params (string Name, string Args)[] calls)
    {
        var r = new AssembledReply { Content = content, FinishReason = calls.Length > 0 ? "tool_calls" : "stop" };
        foreach (var (name, args) in calls) r.Calls.Add(new AssembledCall { Id = "c" + r.Calls.Count, Name = name, Arguments = args });
        return r;
    }

    [Fact(DisplayName = "UNIT-TC-01: broken JSON is repaired by trimming trailing text, closing at the very end or removing a trailing comma")]
    public void UnitTc01()
    {
        Assert.Equal("""{"file_path":"README.md"}""", ToolCallRepair.RepairArguments("""{"file_path":"README.md"} and then I will read it"""));
        Assert.Equal("""{"pattern":"TODO","path":"src"}""", ToolCallRepair.RepairArguments("""{"pattern":"TODO","path":"src",}"""));
        Assert.Equal("""{"command":"dotnet test","cwd":"/work"}""", ToolCallRepair.RepairArguments("""{"command":"dotnet test","cwd":"/work"""));
        Assert.Equal("""{"a":[1,2]}""", ToolCallRepair.RepairArguments("""{"a":[1,2"""));
        Assert.Equal("""{"a":1}""", ToolCallRepair.RepairArguments("""{"a":1,"""));
        Assert.Equal("""{"text":"a,}"}""", ToolCallRepair.RepairArguments("""{"text":"a,}"} trailing"""));
        Assert.Equal("""{"a":1}""", ToolCallRepair.RepairArguments("""{"a":1}}{"b":2"""));
        var reply = Reply(null, ("FileRead", """{"file_path":"README.md"}}"""));
        var (stats, unreadable) = ToolCallRepair.Repair(reply, Offered);
        Assert.Empty(unreadable);
        Assert.Equal(1, stats.Repaired);
        Assert.Equal("""{"file_path":"README.md"}""", reply.Calls[0].Arguments);
    }

    [Fact(DisplayName = "UNIT-TC-02: unrepairable JSON is never guessed and never dropped silently")]
    public void UnitTc02()
    {
        foreach (var bad in new[] { """{"path": src/main.cs}""", """{"a":""", """{"a":1,"b""", """{"a" 1}""", """{"a":[1,}""" })
            Assert.Null(ToolCallRepair.RepairArguments(bad));
        var reply = Reply("Let me look.", ("FileRead", """{"file_path": src/main.cs}"""), ("Grep", """{"pattern":"TODO"}"""));
        var (stats, unreadable) = ToolCallRepair.Repair(reply, Offered);
        Assert.Equal(1, stats.Unreadable);
        Assert.Equal("FileRead", Assert.Single(unreadable).Name);
        Assert.Equal("Grep", Assert.Single(reply.Calls).Name);
        ToolCallRepair.Explain(reply, unreadable);
        Assert.Contains("the call to FileRead was not run because its arguments are not valid JSON", reply.Content);
        Assert.Equal("Grep", Assert.Single(reply.Calls).Name);
    }

    [Fact(DisplayName = "UNIT-TC-03: calls written as text at the end of the reply are recovered for offered tools only")]
    public void UnitTc03()
    {
        var hermes = Reply("I will read it.\n<tool_call>\n{\"name\": \"FileRead\", \"arguments\": {\"file_path\": \"a.md\"}}\n</tool_call>\n");
        var (s1, _) = ToolCallRepair.Repair(hermes, Offered);
        Assert.Equal(1, s1.Recovered);
        Assert.Equal("I will read it.", hermes.Content);
        Assert.Equal(("FileRead", """{"file_path":"a.md"}"""), (hermes.Calls[0].Name, hermes.Calls[0].Arguments));
        Assert.Equal("tool_calls", hermes.FinishReason);

        var coder = Reply("<tool_call>\n<function=Bash>\n<parameter=command>\ndotnet test\n</parameter>\n<parameter=timeout_ms>\n60000\n</parameter>\n</function>\n</tool_call>");
        ToolCallRepair.Repair(coder, Offered);
        Assert.Equal("Bash", coder.Calls[0].Name);
        Assert.Equal("""{"command":"dotnet test","timeout_ms":60000}""", coder.Calls[0].Arguments);

        var two = Reply("<tool_call>{\"name\":\"Grep\",\"arguments\":{\"pattern\":\"a\"}}</tool_call>\n<tool_call>{\"name\":\"Glob\",\"arguments\":{\"pattern\":\"*.cs\"}}</tool_call>");
        ToolCallRepair.Repair(two, Offered);
        Assert.Equal(new[] { "Grep", "Glob" }, two.Calls.Select(c => c.Name));

        var only = Reply("{\"name\": \"ListDirectory\", \"arguments\": {\"path\": \"src\"}}");
        ToolCallRepair.Repair(only, Offered);
        Assert.Equal("ListDirectory", Assert.Single(only.Calls).Name);
        Assert.Null(only.Content);

        var unoffered = Reply("<tool_call>{\"name\":\"DeleteEverything\",\"arguments\":{}}</tool_call>");
        ToolCallRepair.Repair(unoffered, Offered);
        Assert.Empty(unoffered.Calls);
        Assert.Contains("DeleteEverything", unoffered.Content);
        var noTools = Reply("<tool_call>{\"name\":\"FileRead\",\"arguments\":{\"file_path\":\"a\"}}</tool_call>");
        ToolCallRepair.Repair(noTools, new Dictionary<string, JsonObject?>());
        Assert.Empty(noTools.Calls);
    }

    [Fact(DisplayName = "UNIT-TC-04: a call quoted in the middle of a reply stays text")]
    public void UnitTc04()
    {
        var mid = Reply("You call it like <tool_call>{\"name\":\"FileRead\",\"arguments\":{\"file_path\":\"a.md\"}}</tool_call> and then read the result.");
        var (stats, _) = ToolCallRepair.Repair(mid, Offered);
        Assert.Equal(0, stats.Recovered);
        Assert.Empty(mid.Calls);
        Assert.StartsWith("You call it like", mid.Content);
        var json = Reply("The call looks like {\"name\": \"Grep\", \"arguments\": {\"pattern\": \"x\"}} in the docs.");
        ToolCallRepair.Repair(json, Offered);
        Assert.Empty(json.Calls);
    }

    [Fact(DisplayName = "UNIT-TC-05: a call inside reasoning is ignored")]
    public void UnitTc05()
    {
        var thinking = Reply("<think>Maybe <tool_call>{\"name\":\"Bash\",\"arguments\":{\"command\":\"rm -rf build\"}}</tool_call></think>The build folder can stay.");
        ToolCallRepair.Repair(thinking, Offered);
        Assert.Empty(thinking.Calls);
        var onlyInThink = Reply("<think><tool_call>{\"name\":\"Bash\",\"arguments\":{\"command\":\"ls\"}}</tool_call></think>");
        ToolCallRepair.Repair(onlyInThink, Offered);
        Assert.Empty(onlyInThink.Calls);
        var fromJson = ToolCallRepair.FromJson(JsonNode.Parse("""{"choices":[{"message":{"role":"assistant","content":"Done.","reasoning_content":"<tool_call>{\"name\":\"Bash\",\"arguments\":{\"command\":\"ls\"}}</tool_call>"},"finish_reason":"stop"}]}""")!.AsObject());
        ToolCallRepair.Repair(fromJson, Offered);
        Assert.Empty(fromJson.Calls);
        var after = Reply("<think>plan it</think>\n<tool_call>{\"name\":\"Glob\",\"arguments\":{\"pattern\":\"*.md\"}}</tool_call>");
        ToolCallRepair.Repair(after, Offered);
        Assert.Equal("Glob", Assert.Single(after.Calls).Name);
        Assert.Equal("<think>plan it</think>", after.Content);
    }

    [Fact(DisplayName = "UNIT-TC-06: string values become the schema's integer, number or boolean only when they read cleanly")]
    public void UnitTc06()
    {
        var schema = JsonNode.Parse("""
            {"type":"object","properties":{
              "limit":{"type":"integer"},"ratio":{"type":"number"},"force":{"type":"boolean"},"name":{"type":"string"},"maybe":{"type":["integer","null"]},
              "inner":{"type":"object","properties":{"depth":{"type":"integer"}}},"ids":{"type":"array","items":{"type":"integer"}}}}
            """)!.AsObject();
        var args = JsonNode.Parse("""{"limit":"3","ratio":"2.5","force":"TRUE","name":"42","maybe":"7","inner":{"depth":"4"},"ids":["1","x","3"],"extra":"5"}""")!.AsObject();
        Assert.Equal(7, ToolCallRepair.Coerce(args, schema));
        Assert.Equal("""{"limit":3,"ratio":2.5,"force":true,"name":"42","maybe":7,"inner":{"depth":4},"ids":[1,"x",3],"extra":"5"}""", args.ToJsonString());
        var bad = JsonNode.Parse("""{"limit":"three","force":"yes"}""")!.AsObject();
        Assert.Equal(0, ToolCallRepair.Coerce(bad, schema));
        var reply = Reply(null, ("Bash", """{"command":"dotnet test","timeout_ms":"60000"}"""));
        Assert.Equal(1, ToolCallRepair.Repair(reply, Offered).Stats.Coerced);
        Assert.Equal("""{"command":"dotnet test","timeout_ms":60000}""", reply.Calls[0].Arguments);
    }

    [Fact(DisplayName = "UNIT-TC-07: a note is added after three identical calls with identical results, and only then")]
    public void UnitTc07()
    {
        JsonArray History(int calls, bool sameResult, bool sameArgs = true)
        {
            var m = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Find the bug." });
            for (var i = 0; i < calls; i++)
            {
                m.Add(new JsonObject
                {
                    ["role"] = "assistant", ["content"] = "",
                    ["tool_calls"] = new JsonArray(new JsonObject { ["id"] = "t" + i, ["type"] = "function", ["function"] = new JsonObject { ["name"] = "Grep", ["arguments"] = sameArgs ? "{\"pattern\": \"bug\"}" : $"{{\"pattern\":\"bug{i}\"}}" } }),
                });
                m.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = "t" + i, ["content"] = sameResult ? "no matches" : "match " + i });
            }
            return m;
        }
        Assert.Contains("same arguments 3 times", RequestShaper.RepeatNote(History(3, true)));
        Assert.Null(RequestShaper.RepeatNote(History(2, true)));
        Assert.Null(RequestShaper.RepeatNote(History(3, false)));
        Assert.Null(RequestShaper.RepeatNote(History(3, true, sameArgs: false)));
        var body = new JsonObject { ["messages"] = History(4, true) };
        var shaper = new RequestShaper(64, 16384, _ => null);
        var result = shaper.Apply(body, "builder", null);
        Assert.NotNull(result.RepeatNote);
        var lastMessage = body["messages"]!.AsArray().Last()!;
        Assert.EndsWith("Try something different or stop.", lastMessage["content"]!.GetValue<string>());
        Assert.Equal("tool", lastMessage["role"]!.GetValue<string>());
    }

    [Fact(DisplayName = "At most 32 calls are kept from one reply")]
    public void MaxCalls()
    {
        var reply = Reply(null, [.. Enumerable.Range(0, 40).Select(i => ("Glob", $"{{\"pattern\":\"*.{i}\"}}"))]);
        var (stats, _) = ToolCallRepair.Repair(reply, Offered);
        Assert.Equal(32, reply.Calls.Count);
        Assert.Equal(8, stats.Dropped);
    }

    [Fact(DisplayName = "SSE and JSON round trips keep content, reasoning, calls and usage")]
    public void WireForms()
    {
        var sse = string.Concat(
            "data: {\"id\":\"x1\",\"model\":\"m\",\"created\":5,\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"reasoning_content\":\"hm\"}}]}\n\n",
            "data: {\"id\":\"x1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Reading\"}}]}\n\n",
            "data: {\"id\":\"x1\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"FileRead\",\"arguments\":\"{\\\"file_pa\"}}]}}]}\n\n",
            "data: {\"id\":\"x1\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"th\\\":\\\"a.md\\\"}}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\n",
            "data: {\"id\":\"x1\",\"choices\":[],\"usage\":{\"prompt_tokens\":9,\"completion_tokens\":4,\"total_tokens\":13}}\n\n",
            "data: [DONE]\n\n");
        var reply = ToolCallRepair.FromSse(sse);
        Assert.Equal(("hm", "Reading", "{\"file_path\":\"a.md\"}}"), (reply.Reasoning, reply.Content, reply.Calls[0].Arguments));
        ToolCallRepair.Repair(reply, Offered);
        var again = ToolCallRepair.FromSse(ToolCallRepair.ToSse(reply));
        Assert.Equal(("hm", "Reading", "call_1", "{\"file_path\":\"a.md\"}", "tool_calls"), (again.Reasoning, again.Content, again.Calls[0].Id, again.Calls[0].Arguments, again.FinishReason));
        Assert.Equal(13, again.Usage!["total_tokens"]!.GetValue<int>());
        var json = ToolCallRepair.FromJson(ToolCallRepair.ToJson(reply));
        Assert.Equal(reply.Calls[0].Arguments, json.Calls[0].Arguments);
    }

    // ---------------------------------------------------------------- the gateway path

    private (PassThroughGateway Gw, StubHandler Up, List<(string Bot, string Kind, string Detail)> Events, string Token) Gateway(Func<int, string, HttpResponseMessage> answer, bool repair = true, RequestShaper? shaper = null)
    {
        var calls = 0;
        var up = new StubHandler((_, body) => Task.FromResult(answer(Interlocked.Increment(ref calls), body)));
        var events = new List<(string, string, string)>();
        var token = "omb_" + new string('g', 32);
        var routes = new Dictionary<string, ModelRoute>(StringComparer.OrdinalIgnoreCase) { ["flash-next"] = new("flash-next", "Qwen3.8-Flash-Next", "", "https://relay.example.test/v1", "", ["class=main-3090"], 8192) };
        var gw = new PassThroughGateway(() => new Dictionary<string, string> { [SecureFile.Sha256Hex(token)] = "builder" }, () => routes, _ => null, new HttpClient(up), _r.Clock, false, TimeSpan.FromSeconds(30))
        {
            RepairToolCalls = repair,
            Shaper = shaper,
            OnEvent = (b, k, d) => { lock (events) events.Add((b, k, d)); },
        };
        return (gw, up, events, token);
    }

    private static async Task<(int Status, string Body, string Type)> Post(PassThroughGateway gw, string token, JsonObject body)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/v1/chat/completions";
        ctx.Request.Headers.Authorization = "Bearer " + token;
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body.ToJsonString()));
        ctx.Response.Body = new MemoryStream();
        await gw.HandleAsync(ctx);
        ctx.Response.Body.Position = 0;
        return (ctx.Response.StatusCode, await new StreamReader(ctx.Response.Body).ReadToEndAsync(), ctx.Response.ContentType ?? "");
    }

    private static JsonObject Ask(bool stream, bool tools = true) => new()
    {
        ["model"] = "flash-next",
        ["stream"] = stream,
        ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Read a.md" }),
        ["tools"] = tools ? ToolBench.Tools() : null,
    };

    private static HttpResponseMessage Completion(string? content, string? name = null, string? args = null)
    {
        var message = new JsonObject { ["role"] = "assistant", ["content"] = content };
        if (name is not null) message["tool_calls"] = new JsonArray(new JsonObject { ["id"] = "call_9", ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args } });
        return StubHandler.Json(new JsonObject
        {
            ["id"] = "r", ["object"] = "chat.completion", ["model"] = "Qwen3.8-Flash-Next",
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = name is null ? "stop" : "tool_calls" }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 5, ["total_tokens"] = 15 },
        }.ToJsonString());
    }

    [Fact(DisplayName = "Gateway: a broken streamed call reaches OMA repaired, and a text call reaches it as a real call")]
    public async Task GatewayRepairsStream()
    {
        var (gw, up, events, token) = Gateway((n, _) => StubHandler.Sse(
            "{\"id\":\"s\",\"model\":\"Qwen\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"FileRead\",\"arguments\":\"{\\\"file_path\\\":\\\"a.md\\\"} oops\"}}]}}]}",
            "{\"id\":\"s\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}",
            "[DONE]"));
        var (status, body, type) = await Post(gw, token, Ask(stream: true));
        Assert.Equal(200, status);
        Assert.StartsWith("text/event-stream", type);
        var reply = ToolCallRepair.FromSse(body);
        Assert.Equal("{\"file_path\":\"a.md\"}", Assert.Single(reply.Calls).Arguments);
        Assert.Contains(events, e => e.Kind == "tool_calls_repaired");
        Assert.Single(up.Seen);

        var (gw2, _, _, token2) = Gateway((_, _) => Completion("Reading.\n<tool_call>{\"name\":\"FileRead\",\"arguments\":{\"file_path\":\"a.md\",\"limit\":\"20\"}}</tool_call>"));
        var (_, body2, _) = await Post(gw2, token2, Ask(stream: false));
        var r2 = ToolCallRepair.FromJson(JsonNode.Parse(body2)!.AsObject());
        Assert.Equal(("FileRead", "{\"file_path\":\"a.md\",\"limit\":20}", "Reading.", "tool_calls"), (r2.Calls[0].Name, r2.Calls[0].Arguments, r2.Content, r2.FinishReason));
    }

    [Fact(DisplayName = "Gateway: an unreadable call is sent back once with a note; still unreadable becomes text, never a silent drop")]
    public async Task GatewayResendsOnce()
    {
        var (gw, up, events, token) = Gateway((n, _) => n == 1 ? Completion(null, "FileRead", "{\"file_path\": a.md}") : Completion(null, "FileRead", "{\"file_path\":\"a.md\"}"));
        var (_, body, _) = await Post(gw, token, Ask(stream: false));
        Assert.Equal(2, up.Seen.Count);
        Assert.Contains("not valid JSON", up.Seen[1].Body);
        Assert.Equal("{\"file_path\":\"a.md\"}", ToolCallRepair.FromJson(JsonNode.Parse(body)!.AsObject()).Calls[0].Arguments);
        Assert.Contains(events, e => e.Kind == "tool_args_unreadable" && e.Detail == "FileRead");

        var (gw2, up2, events2, token2) = Gateway((_, _) => Completion("Trying.", "FileRead", "{\"file_path\": a.md}"));
        var (_, body2, _) = await Post(gw2, token2, Ask(stream: false));
        Assert.Equal(2, up2.Seen.Count);
        var r2 = ToolCallRepair.FromJson(JsonNode.Parse(body2)!.AsObject());
        Assert.Empty(r2.Calls);
        Assert.Contains("the call to FileRead was not run", r2.Content);
        Assert.Equal("stop", r2.FinishReason);
        Assert.Contains(events2, e => e.Kind == "tool_args_unreadable_after_resend");
    }

    [Fact(DisplayName = "Gateway: replies to requests without tools, or with repair off, pass through byte for byte")]
    public async Task GatewayPassThrough()
    {
        const string raw = "data: {\"choices\":[{\"delta\":{\"content\":\"<tool_call>{\\\"name\\\":\\\"FileRead\\\"}</tool_call>\"}}]}\n\ndata: [DONE]\n\n";
        var (gw, _, _, token) = Gateway((_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(raw, Encoding.UTF8, "text/event-stream") });
        Assert.Equal(raw, (await Post(gw, token, Ask(stream: true, tools: false))).Body);
        var (off, _, _, token2) = Gateway((_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(raw, Encoding.UTF8, "text/event-stream") }, repair: false);
        Assert.Equal(raw, (await Post(off, token2, Ask(stream: true))).Body);
    }

    [Fact(DisplayName = "Shaping: tools over the limit are left out, output is capped at a quarter of the context, long results spill to a 0600 file")]
    public async Task Shaping()
    {
        var spill = Path.Combine(_r.Paths.Bot("builder").Data, "spill");
        var shaper = new RequestShaper(5, 4096, slug => slug == "builder" ? (spill, "/home/agent/.openmono/spill") : null);
        var (gw, up, events, token) = Gateway((_, _) => Completion("ok"), shaper: shaper);
        var big = string.Concat(Enumerable.Range(0, 2000).Select(i => $"line {i}\n"));
        var ask = Ask(stream: false);
        ask["max_tokens"] = 100000;
        ((JsonArray)ask["messages"]!).Add(new JsonObject { ["role"] = "assistant", ["tool_calls"] = new JsonArray(new JsonObject { ["id"] = "t1", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "Bash", ["arguments"] = "{\"command\":\"cat big.log\"}" } }) });
        ((JsonArray)ask["messages"]!).Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = "t1", ["content"] = big });
        await Post(gw, token, ask);
        var sent = JsonNode.Parse(up.Seen.Single().Body)!.AsObject();
        Assert.Equal(5, sent["tools"]!.AsArray().Count);
        Assert.Equal(2048, sent["max_tokens"]!.GetValue<int>());
        var tool = sent["messages"]!.AsArray().Last()!["content"]!.GetValue<string>();
        Assert.StartsWith("line 0\n", tool);
        Assert.Contains("/home/agent/.openmono/spill/", tool);
        Assert.EndsWith("line 1999\n", tool);
        Assert.True(tool.Length < big.Length / 4);
        var file = Assert.Single(Directory.GetFiles(spill));
        Assert.Equal(big, File.ReadAllText(file));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        await Post(gw, token, ask);
        Assert.Single(Directory.GetFiles(spill));
        Assert.Contains(events, e => e.Kind == "tools_trimmed");
        Assert.Contains(events, e => e.Kind == "tool_result_spilled");
    }

    [Fact(DisplayName = "Presets: every rendered preset carries the gateway endpoint and the bot's model, and the container names the bot's preset")]
    public async Task Presets()
    {
        var env = new CliEnv { Paths = _r.Paths, AppDir = _r.AppDir, Clock = _r.Clock, Out = new StringWriter(), Err = new StringWriter() };
        var team = "version: 1\nmodels:\n  flash-next:\n    upstream_model: Qwen3.8-Flash-Next\n    context_size: 32768\nbots:\n  - name: Builder\n    personality: You build.\n    model: flash-next\n    preset: tools\n    tools: { allow: [FileRead] }\n";
        Assert.Equal(0, await new CliApp(env).RunAsync(["team", "apply", _r.TeamFile(team)]));
        var bad = new CliEnv { Paths = _r.Paths, AppDir = _r.AppDir, Clock = _r.Clock, Out = new StringWriter(), Err = new StringWriter() };
        Assert.NotEqual(0, await new CliApp(bad).RunAsync(["team", "apply", _r.TeamFile(team.Replace("preset: tools", "preset: wild", StringComparison.Ordinal))]));
        Assert.Contains("No preset \"wild\"", bad.Err.ToString());
        var db = _r.Db();
        var row = new BotStore(db, _r.Clock).Get("builder");
        var renderer = new BotRenderer(_r.Paths, _r.Config);
        var settings = JsonNode.Parse(renderer.RenderSettings(row.Spec, new RenderContext("http://172.30.240.1:7478/v1", false, 32768, [])))!;
        foreach (var name in new[] { "qwen", "qwen38", "chat", "tools" })
        {
            var p = settings["model_presets"]![name]!;
            Assert.Equal("http://172.30.240.1:7478/v1", p["endpoint"]!.GetValue<string>());
            Assert.Equal("flash-next", p["model"]!.GetValue<string>());
            Assert.Equal(32768, p["context_size"]!.GetValue<int>());
            Assert.Equal(8192, p["max_output_tokens"]!.GetValue<int>());
        }
        Assert.Equal(0.4, settings["model_presets"]!["tools"]!["temperature"]!.GetValue<double>());
        var spec = ContainerSpec.For(row.Spec, row.Port, "omb_t" + new string('q', 30), _r.Paths, _r.Config, renderer, _r.AppDir, false);
        Assert.Equal("tools", spec.Env["OPENMONO_MODEL_PRESET"]);
    }

    [Fact(DisplayName = "Tool task set: 40 tasks, scored before and after repair against a scripted model at 1, 2 and 4 parallel requests")]
    public async Task ToolTaskSet()
    {
        var tasks = ToolBench.Tasks();
        Assert.Equal(40, tasks.Count);
        Assert.Equal(40, tasks.Select(t => t.Id).Distinct().Count());
        var byPrompt = tasks.ToDictionary(t => t.Prompt);
        // A scripted model: right calls, but every fourth has trailing text, every fifth is written as text, typed values come as strings.
        HttpResponseMessage Model(string body)
        {
            var prompt = JsonNode.Parse(body)!["messages"]![1]!["content"]!.GetValue<string>();
            var task = byPrompt[prompt];
            var index = tasks.ToList().IndexOf(task);
            var args = Arguments(task);
            if (index % 5 == 4) return Completion($"<tool_call>{{\"name\":\"{task.Tool}\",\"arguments\":{args.ToJsonString()}}}</tool_call>");
            var text = args.ToJsonString();
            if (task.Id.StartsWith("typed", StringComparison.Ordinal)) text = System.Text.RegularExpressions.Regex.Replace(text, ":(\\d+)", ":\"$1\"");
            if (index % 4 == 3) text += " done";
            return Completion(null, task.Tool, text);
        }
        var up = new StubHandler((_, body) => Task.FromResult(Model(body)));
        var scores = await ToolBench.RunAsync(new HttpClient(up), "https://relay.example.test/v1", null, "Qwen3.8-Flash-Next", [1, 2, 4], CancellationToken.None);
        Assert.Equal(120, scores.Count);
        var levels = ToolBench.Summarize(scores);
        Assert.Equal(new[] { 1, 2, 4 }, levels.Select(l => l.Slots));
        Assert.All(levels, l =>
        {
            Assert.Equal(100, l.ValidAfter);
            Assert.True(l.ValidBefore < 70, $"before {l.ValidBefore}");
            Assert.Equal(0, l.Unreadable);
        });
        var report = ToolBench.Report(scores, "Qwen3.8-Flash-Next", _r.Clock.UtcNow);
        Assert.Contains("| 4 | 40 |", report);
    }

    private static JsonObject Arguments(BenchTask t)
    {
        var p = t.Prompt;
        string Quoted(int n = 0) => System.Text.RegularExpressions.Regex.Matches(p, "\"([^\"]*)\"")[n].Groups[1].Value;
        string Ticked() => System.Text.RegularExpressions.Regex.Match(p, "`([^`]*)`").Groups[1].Value;
        return t.Tool switch
        {
            "FileRead" when t.Id == "typed-3" => new JsonObject { ["file_path"] = "README.md", ["offset"] = 10, ["limit"] = 20 },
            "FileRead" => new JsonObject { ["file_path"] = p["Read the file ".Length..^1] },
            "Grep" when t.Id == "typed-2" => new JsonObject { ["pattern"] = "TODO", ["max_results"] = 5 },
            "Grep" when t.Id == "typed-5" => new JsonObject { ["pattern"] = "Skip", ["path"] = "tests", ["max_results"] = 10 },
            "Grep" => new JsonObject { ["pattern"] = Quoted(), ["path"] = "src" },
            "ListDirectory" => new JsonObject { ["path"] = p["List the ".Length..p.IndexOf(" folder", StringComparison.Ordinal)] },
            "Glob" => new JsonObject { ["pattern"] = p.Contains(".sh", StringComparison.Ordinal) ? "**/*.sh" : "**/*.csproj" },
            "FileEdit" when t.Id == "edit-5" => new JsonObject { ["file_path"] = "config.json", ["old_string"] = "\"debug\": true", ["new_string"] = "\"debug\": false" },
            "FileEdit" => new JsonObject { ["file_path"] = p[3..p.IndexOf(',', StringComparison.Ordinal)], ["old_string"] = Quoted(0), ["new_string"] = Quoted(1) },
            "Bash" when t.Id == "typed-1" => new JsonObject { ["command"] = "dotnet test", ["timeout_ms"] = 60000 },
            "Bash" when t.Id == "typed-4" => new JsonObject { ["command"] = "git log -5", ["timeout_ms"] = 5000 },
            "Bash" => new JsonObject { ["command"] = Ticked() },
            _ => new JsonObject { ["to"] = p.Split(' ')[p.StartsWith("Ask", StringComparison.Ordinal) ? 1 : 4].TrimEnd(':'), ["brief"] = p },
        };
    }

    [Fact(DisplayName = "LOAD-04: the tool task set at 1, 2 and 4 slots against the real inference server, valid call rate before and after repair", Skip = "Needs the inference server (Qwen3.8-Flash-Next on Strata) through the FRP relay; run oma-bot gateway toolbench --save on the main box. The scorer and repair rates are covered with a scripted model above.")]
    public void Load04() { }
}
