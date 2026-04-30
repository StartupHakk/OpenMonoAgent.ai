using System.Text.Json;
using OpenMono.Permissions;

namespace OpenMono.Tools;

public enum PermissionLevel
{
    AutoAllow,
    Ask,
    Deny
}

public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonElement InputSchema { get; }
    bool IsConcurrencySafe { get; }
    bool IsReadOnly { get; }

    bool IsDeferred => false;

    Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct);

    PermissionLevel RequiredPermission(JsonElement input);

    IReadOnlyList<Capability> RequiredCapabilities(JsonElement input) => [];
}
