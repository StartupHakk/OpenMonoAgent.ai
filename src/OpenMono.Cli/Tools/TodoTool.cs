using System.Text.Json;
using OpenMono.Permissions;

namespace OpenMono.Tools;

public sealed class TodoTool : ToolBase
{
    public override string Name => "TodoWrite";
    public override string Description => "Create and manage a task checklist to track progress on complex tasks.";
    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

    public override bool IsReadOnly => true;

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddArray("todos", "List of todo items", new
        {
            type = "object",
            properties = new
            {
                content = new { type = "string", description = "Task description" },
                status = new { type = "string", @enum = new[] { "pending", "in_progress", "completed" } },
                active_form = new { type = "string", description = "Present tense form shown during execution" }
            },
            required = new[] { "content", "status" }
        })
        .Require("todos");

    public IReadOnlyList<Capability> RequiredCapabilities(JsonElement input) => [];

    protected override Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var todosElement = input.GetProperty("todos");
        var todos = new List<Session.TodoItem>();

        foreach (var item in todosElement.EnumerateArray())
        {
            todos.Add(new Session.TodoItem
            {
                Content = item.GetProperty("content").GetString()!,
                Status = item.GetProperty("status").GetString()!,
                ActiveForm = item.TryGetProperty("active_form", out var af) ? af.GetString() : null,
            });
        }

        context.Session.Todos.Clear();
        context.Session.Todos.AddRange(todos);

        var completed = todos.Count(t => t.Status == "completed");
        var inProgress = todos.Count(t => t.Status == "in_progress");
        var pending = todos.Count(t => t.Status == "pending");

        return Task.FromResult(ToolResult.Success(
            $"Todo list updated: {completed} completed, {inProgress} in progress, {pending} pending"));
    }
}
