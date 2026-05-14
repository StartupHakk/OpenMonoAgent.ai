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

    private static readonly SemaphoreSlim _globalSlot = new(1, 1);

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
        var maxDepth = context.Config.Agents.MaxNestingDepth;
        if (depth >= maxDepth)
            return ToolResult.Error(
                $"Agent nesting depth limit ({maxDepth}) reached at depth {depth}. " +
                "Sub-agents cannot spawn further sub-agents beyond this level.");

        context.WriteOutput($"[Agent: {description}] Queuing {agentType} sub-agent (depth {depth})...");
        await _globalSlot.WaitAsync(ct);
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

            var sink = new SubAgentOutputSink(description, context.WriteOutput);
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
            _globalSlot.Release();
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
