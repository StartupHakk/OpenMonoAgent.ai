using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenMono.Tools;

public static class SchemaValidator
{
    public static string? Validate(string toolName, JsonElement schema, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
            return $"{toolName}: expected JSON object, got {input.ValueKind}";

        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var field in required.EnumerateArray())
            {
                var fieldName = field.GetString();
                if (fieldName is null) continue;
                if (!input.TryGetProperty(fieldName, out var present) || present.ValueKind == JsonValueKind.Null)
                    return $"{toolName}: missing required field '{fieldName}'";
            }
        }

        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var prop in properties.EnumerateObject())
        {
            if (!input.TryGetProperty(prop.Name, out var value) || value.ValueKind == JsonValueKind.Null)
                continue;

            var error = ValidateProperty(toolName, prop.Name, prop.Value, value);
            if (error is not null) return error;
        }

        return null;
    }

    private static string? ValidateProperty(string toolName, string fieldName, JsonElement propSchema, JsonElement value)
    {
        if (propSchema.TryGetProperty("type", out var typeEl))
        {
            var expected = typeEl.GetString();
            var typeError = ValidateType(toolName, fieldName, expected, value);
            if (typeError is not null) return typeError;
        }

        if (propSchema.TryGetProperty("enum", out var enumEl) && enumEl.ValueKind == JsonValueKind.Array)
        {
            var allowed = enumEl.EnumerateArray().Select(e => e.ToString()).ToList();
            if (!allowed.Contains(value.ToString()))
                return $"{toolName}: field '{fieldName}' must be one of [{string.Join(", ", allowed)}], got '{value}'";
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            if (propSchema.TryGetProperty("minimum", out var minEl) && minEl.TryGetDouble(out var min) && value.GetDouble() < min)
                return $"{toolName}: field '{fieldName}' = {value.GetDouble()} is below minimum {min}";

            if (propSchema.TryGetProperty("maximum", out var maxEl) && maxEl.TryGetDouble(out var max) && value.GetDouble() > max)
                return $"{toolName}: field '{fieldName}' = {value.GetDouble()} is above maximum {max}";
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var s = value.GetString() ?? "";

            if (propSchema.TryGetProperty("minLength", out var minLenEl) && minLenEl.TryGetInt32(out var minLen) && s.Length < minLen)
                return $"{toolName}: field '{fieldName}' is shorter than minLength {minLen}";

            if (propSchema.TryGetProperty("maxLength", out var maxLenEl) && maxLenEl.TryGetInt32(out var maxLen) && s.Length > maxLen)
                return $"{toolName}: field '{fieldName}' is longer than maxLength {maxLen}";

            if (propSchema.TryGetProperty("pattern", out var patEl) && patEl.GetString() is { } pattern)
            {
                try
                {
                    if (!Regex.IsMatch(s, pattern))
                        return $"{toolName}: field '{fieldName}' does not match pattern /{pattern}/";
                }
                catch (RegexParseException)
                {

                }
            }
        }

        if (value.ValueKind == JsonValueKind.Array && propSchema.TryGetProperty("items", out var itemsSchema))
        {
            var i = 0;
            foreach (var item in value.EnumerateArray())
            {
                var itemError = ValidateProperty(toolName, $"{fieldName}[{i}]", itemsSchema, item);
                if (itemError is not null) return itemError;
                i++;
            }
        }

        return null;
    }

    private static string? ValidateType(string toolName, string fieldName, string? expected, JsonElement value)
    {
        if (expected is null) return null;

        var actual = value.ValueKind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => value.TryGetInt64(out _) ? "integer" : "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Array => "array",
            JsonValueKind.Object => "object",
            _ => "unknown",
        };

        var ok = expected switch
        {
            "string" => actual == "string",
            "integer" => actual == "integer",
            "number" => actual is "integer" or "number",
            "boolean" => actual == "boolean",
            "array" => actual == "array",
            "object" => actual == "object",
            _ => true,
        };

        return ok ? null : $"{toolName}: field '{fieldName}' expected type '{expected}', got '{actual}'";
    }
}
