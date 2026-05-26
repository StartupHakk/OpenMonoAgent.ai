using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenMono.Agents;
using OpenMono.Llm;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;

namespace OpenMono.Tools;

public sealed class AgentTool : ToolBase
{
    public override string Name => "Agent";
    public override string Description => "Spawn a sub-agent to handle a complex task. The sub-agent has its own conversation context and returns a summary when done.";
    public override bool IsConcurrencySafe => true;

    private static SemaphoreSlim? _slot;
    private static int _slotCapacity;
    private static int _queued;
    private static readonly object _slotInitLock = new();
    private static readonly ConditionalWeakTable<SessionState, StrongBox<int>> _perParent = new();

    private static SemaphoreSlim EnsureSlot(int requested)
    {
        var capacity = Math.Max(1, requested);
        if (_slot is { } existing && _slotCapacity == capacity)
            return existing;
        lock (_slotInitLock)
        {
            if (_slot is null || _slotCapacity != capacity)
            {
                _slot?.Dispose();
                _slot = new SemaphoreSlim(capacity, capacity);
                _slotCapacity = capacity;
            }
            return _slot;
        }
    }

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("description", "Short description of the task (3-5 words)")
        .AddString("prompt", "Detailed instructions for the sub-agent")
        .AddEnum("agent_type", "Agent type determines available tools (default: general-purpose)",
            "general-purpose", "Explore", "Plan", "Coder", "Verify")
        .Require("description", "prompt");

    public IReadOnlyList<Capability> RequiredCapabilities(JsonElement input)
    {
        var description = input.TryGetProperty("description", out var d) ? d.GetString() : "task";
        var agentType = input.TryGetProperty("agent_type", out var at) ? at.GetString() : "general-purpose";
        return [new AgentSpawnCap(agentType ?? "general-purpose", description ?? "task")];
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var description = input.GetProperty("description").GetString()!;
        var prompt = input.GetProperty("prompt").GetString()!;
        var agentType = input.TryGetProperty("agent_type", out var at) ? at.GetString()! : "general-purpose";

        if (!BuiltInAgents.All.TryGetValue(agentType, out var agentDef))
            return ToolResult.Error($"Unknown agent type: {agentType}. Valid: {string.Join(", ", BuiltInAgents.All.Keys)}");

        var depth = context.AgentDepth;
        var agentsCfg = context.Config.Agents;
        if (depth >= agentsCfg.MaxNestingDepth)
            return ToolResult.Error(
                $"Agent nesting depth limit ({agentsCfg.MaxNestingDepth}) reached at depth {depth}. " +
                "Sub-agents cannot spawn further sub-agents beyond this level.");

        var perParent = _perParent.GetValue(context.Session, _ => new StrongBox<int>(0));
        if (Volatile.Read(ref perParent.Value) >= agentsCfg.MaxConcurrentPerParent)
            return ToolResult.Error(
                $"Per-parent sub-agent fan-out limit ({agentsCfg.MaxConcurrentPerParent}) reached. " +
                "Wait for an in-flight sub-agent from this conversation to finish before spawning another.");

        var newQueued = Interlocked.Increment(ref _queued);
        if (newQueued > agentsCfg.MaxQueuedAgents)
        {
            Interlocked.Decrement(ref _queued);
            return ToolResult.Error(
                $"Sub-agent queue is full ({agentsCfg.MaxQueuedAgents}). " +
                "Too many sub-agents are already waiting; try again after some complete.");
        }

        Interlocked.Increment(ref perParent.Value);
        var slot = EnsureSlot(agentsCfg.MaxConcurrentAgents);
        context.WriteOutput($"[Agent: {description}] Queuing {agentType} sub-agent (depth {depth}, queued {newQueued}/{agentsCfg.MaxQueuedAgents})...");
        try
        {
            await slot.WaitAsync(ct);
        }
        catch
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Decrement(ref perParent.Value);
            throw;
        }
        Interlocked.Decrement(ref _queued);
        try
        {
            context.WriteOutput($"[Agent: {description}] Starting...");

            var subSession = new SessionState();
            var systemPrompt = agentDef.SystemPrompt
                ?? "You are a helpful coding assistant. Complete the task described below.";
            subSession.AddMessage(new Message { Role = MessageRole.System, Content = systemPrompt });

            var subTools = new ToolRegistry();
            foreach (var tool in context.ToolRegistry.All)
            {
                if (tool.Name == "Agent") continue;
                if (IsToolAllowed(tool.Name, agentDef.AllowedTools))
                    subTools.Register(tool);
            }

            var sink = new SubAgentOutputSink(description, context.WriteOutput, context.Output);
            var inputReader = new NullInputReader();
            var llm = new OpenAiCompatClient(context.Config.Llm) { ApiKey = context.Config.Llm.ApiKey };

            try
            {
                using var childLoop = new ConversationLoop(
                    llm:           llm,
                    tools:         subTools,
                    permissions:   context.Permissions,
                    output:        sink,
                    input:         inputReader,
                    liveFeedback:  null,
                    config:        context.Config,
                    session:       subSession,
                    maxIterations: agentDef.MaxTurns,
                    agentDepth:    depth + 1);

                await childLoop.RunTurnAsync(prompt, ct);
            }
            finally
            {
                llm.Dispose();
            }

            var result = sink.CapturedText.Trim();
            if (string.IsNullOrEmpty(result))
                result = "Sub-agent completed but produced no text output. Check tool results above.";

            return ToolResult.Success(
                $"[Sub-agent '{description}' ({agentType}) completed]\n\n{result}");
        }
        finally
        {
            slot.Release();
            Interlocked.Decrement(ref perParent.Value);
            context.Output?.ClearWaitingIndicator();
            context.WriteOutput($"[Agent: {description}] Done.");
        }
    }

    private static bool IsToolAllowed(string toolName, string[] allowedTools)
    {
        foreach (var entry in allowedTools)
        {
            if (entry == "*") return true;
            if (entry.EndsWith('*'))
            {
                var prefix = entry[..^1];
                if (toolName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (toolName.Equals(entry, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
