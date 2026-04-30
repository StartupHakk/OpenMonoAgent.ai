using System.Text.Json;

namespace OpenMono.Tools;

public abstract class ToolBase : ITool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public virtual bool IsConcurrencySafe => false;
    public virtual bool IsReadOnly => false;
    public virtual bool IsDeferred => false;
    public virtual PermissionLevel DefaultPermission => PermissionLevel.Ask;

    private JsonElement? _cachedSchema;

    public JsonElement InputSchema => _cachedSchema ??= DefineSchema().Build();

    protected abstract SchemaBuilder DefineSchema();

    public virtual PermissionLevel RequiredPermission(JsonElement input) => DefaultPermission;

    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct)
        => ExecuteCoreAsync(input, context, ct);

    protected abstract Task<ToolResult> ExecuteCoreAsync(
        JsonElement input, ToolContext context, CancellationToken ct);
}

public sealed class SchemaBuilder
{
    private readonly Dictionary<string, object> _properties = new();
    private readonly List<string> _required = [];

    public SchemaBuilder AddString(string name, string description)
    {
        _properties[name] = new { type = "string", description };
        return this;
    }

    public SchemaBuilder AddInteger(string name, string description, int? minimum = null, int? maximum = null)
    {
        var prop = new Dictionary<string, object> { ["type"] = "integer", ["description"] = description };
        if (minimum.HasValue) prop["minimum"] = minimum.Value;
        if (maximum.HasValue) prop["maximum"] = maximum.Value;
        _properties[name] = prop;
        return this;
    }

    public SchemaBuilder AddBoolean(string name, string description)
    {
        _properties[name] = new { type = "boolean", description };
        return this;
    }

    public SchemaBuilder AddEnum(string name, string description, params string[] values)
    {
        _properties[name] = new { type = "string", description, @enum = values };
        return this;
    }

    public SchemaBuilder AddArray(string name, string description, object itemSchema)
    {
        _properties[name] = new { type = "array", description, items = itemSchema };
        return this;
    }

    public SchemaBuilder AddProperty(string name, object schema)
    {
        _properties[name] = schema;
        return this;
    }

    public SchemaBuilder Require(params string[] names)
    {
        _required.AddRange(names);
        return this;
    }

    public JsonElement Build()
    {
        var schema = new
        {
            type = "object",
            properties = _properties,
            required = _required.Count > 0 ? _required : null
        };

        var json = JsonSerializer.Serialize(schema, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
