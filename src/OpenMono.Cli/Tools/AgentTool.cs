using System.Text;
using System.Text.Json;
using OpenMono.Agents;
using OpenMono.Llm;
using OpenMono.Permissions;
using OpenMono.Session;

namespace OpenMono.Tools;

public sealed class AgentTool : ToolBase
{
    public override string Name => "Agent";
    public override string Description => "Spawn a sub-agent to handle a complex task. The sub-agent has its own conversation context and returns a summary when done.";
    public override bool IsConcurrencySafe => true;

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

        context.WriteOutput($"[Agent: {description}] Starting {agentType} sub-agent...");

        var subTools = new ToolRegistry();
        foreach (var tool in context.ToolRegistry.All)
        {
            if (tool.Name == "Agent") continue;
            if (IsToolAllowed(tool.Name, agentDef.AllowedTools))
                subTools.Register(tool);
        }

        var subSession = new SessionState();
        var systemPrompt = agentDef.SystemPrompt
            ?? "You are a helpful coding assistant. Complete the task described below.";
        subSession.AddMessage(new Message { Role = MessageRole.System, Content = systemPrompt });
        subSession.AddMessage(new Message { Role = MessageRole.User, Content = prompt });

        var resultBuffer = new StringBuilder();
        var toolDefs = subTools.BuildToolDefinitions();
        var options = new LlmOptions
        {
            Model = context.Config.Llm.Model,
            Temperature = context.Config.Llm.Temperature,
            MaxTokens = context.Config.Llm.MaxOutputTokens,
        };

        var llm = new OpenAiCompatClient(context.Config.Llm);

        try
        {
            for (var turn = 0; turn < agentDef.MaxTurns; turn++)
            {
                ct.ThrowIfCancellationRequested();

                var textBuffer = new StringBuilder();
                var toolCalls = new List<ToolCall>();

                await foreach (var chunk in llm.StreamChatAsync(subSession.Messages, toolDefs, options, ct))
                {
                    if (chunk.TextDelta is not null)
                        textBuffer.Append(chunk.TextDelta);
                    if (chunk.ToolCallDelta is not null)
                        toolCalls.Add(chunk.ToolCallDelta);
                    if (chunk.IsComplete)
                        break;
                }

                subSession.AddMessage(new Message
                {
                    Role = MessageRole.Assistant,
                    Content = textBuffer.Length > 0 ? textBuffer.ToString() : null,
                    ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
                });

                if (toolCalls.Count == 0)
                {
                    resultBuffer.Append(textBuffer);
                    break;
                }

                foreach (var call in toolCalls)
                {
                    var tool = subTools.Resolve(call.Name);
                    if (tool is null)
                    {
                        subSession.AddMessage(new Message
                        {
                            Role = MessageRole.Tool,
                            ToolCallId = call.Id,
                            Content = $"Unknown tool: {call.Name}",
                        });
                        continue;
                    }

                    JsonElement toolInput;
                    try { toolInput = JsonDocument.Parse(call.Arguments).RootElement; }
                    catch (JsonException) { toolInput = JsonDocument.Parse("{}").RootElement; }

                    var permLevel = tool.RequiredPermission(toolInput);
                    var decision = await context.Permissions.CheckAsync(tool.Name, toolInput, permLevel, ct);

                    ToolResult toolResult;
                    if (!decision.Allowed)
                    {
                        toolResult = ToolResult.Error($"Permission denied: {decision.Reason}");
                    }
                    else
                    {
                        try { toolResult = await tool.ExecuteAsync(toolInput, context, ct); }
                        catch (Exception ex) { toolResult = ToolResult.Error(ex.Message); }
                    }

                    subSession.AddMessage(new Message
                    {
                        Role = MessageRole.Tool,
                        ToolCallId = call.Id,
                        ToolName = call.Name,
                        Content = toolResult.Content,
                    });
                }
            }
        }
        finally
        {
            llm.Dispose();
        }

        var result = resultBuffer.Length > 0
            ? resultBuffer.ToString()
            : "Sub-agent completed but produced no text output. Check tool results above.";

        return ToolResult.Success($"[Sub-agent '{description}' ({agentType}) completed]\n\n{result}");
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
