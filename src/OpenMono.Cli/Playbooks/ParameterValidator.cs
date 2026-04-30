namespace OpenMono.Playbooks;

public static class ParameterValidator
{

    public static string? Validate(
        PlaybookDefinition playbook,
        Dictionary<string, object> parameters)
    {
        foreach (var (name, def) in playbook.Parameters)
        {
            var hasValue = parameters.TryGetValue(name, out var value);

            if (def.Required && (!hasValue || value is null))
            {
                if (def.Default is not null)
                {
                    parameters[name] = def.Default;
                    continue;
                }
                return $"Required parameter '{name}' is missing. {def.Hint ?? ""}".Trim();
            }

            if (!hasValue && def.Default is not null)
            {
                parameters[name] = def.Default;
                continue;
            }

            if (!hasValue || value is null) continue;

            var typeError = ValidateType(name, value, def);
            if (typeError is not null) return typeError;

            if (def.Enum is not null)
            {
                var strVal = value.ToString() ?? "";
                if (!def.Enum.Contains(strVal, StringComparer.OrdinalIgnoreCase))
                    return $"Parameter '{name}' must be one of: {string.Join(", ", def.Enum)}. Got: {strVal}";
            }

            if (def.Type == ParameterType.Number && value is double numVal)
            {
                if (def.Min.HasValue && numVal < def.Min.Value)
                    return $"Parameter '{name}' must be >= {def.Min.Value}. Got: {numVal}";
                if (def.Max.HasValue && numVal > def.Max.Value)
                    return $"Parameter '{name}' must be <= {def.Max.Value}. Got: {numVal}";
            }
        }

        return null;
    }

    private static string? ValidateType(string name, object value, ParameterDefinition def)
    {
        return def.Type switch
        {
            ParameterType.String when value is not string =>
                $"Parameter '{name}' must be a string. Got: {value.GetType().Name}",
            ParameterType.Number when value is not (double or int or long or float) =>
                $"Parameter '{name}' must be a number. Got: {value.GetType().Name}",
            ParameterType.Boolean when value is not bool =>
                $"Parameter '{name}' must be a boolean. Got: {value.GetType().Name}",
            _ => null,
        };
    }
}
