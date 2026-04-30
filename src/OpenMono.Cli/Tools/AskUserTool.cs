using System.Text.Json;
using OpenMono.Permissions;

namespace OpenMono.Tools;

public sealed class AskUserTool : ToolBase
{
    public override string Name => "AskUser";
    public override string Description => "Ask the user a question and wait for their response. Use when you need clarification or a decision.";
    public override bool IsReadOnly => true;
    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("question", "The question to ask the user")
        .AddArray("options", "Optional list of choices for the user to pick from", new { type = "string" })
        .Require("question");

    public IReadOnlyList<Capability> RequiredCapabilities(JsonElement input) => [];

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var question = input.GetProperty("question").GetString()!;
        var hasOptions = input.TryGetProperty("options", out var opts);

        var prompt = question;
        if (hasOptions)
        {
            var options = opts.EnumerateArray().Select(o => o.GetString()!).ToList();
            prompt += "\n" + string.Join('\n', options.Select((o, i) => $"  [{i + 1}] {o}"));
        }

        var response = await context.AskUser(prompt, ct);
        return ToolResult.Success(response);
    }
}
