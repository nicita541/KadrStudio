using System.Text.Json;

namespace KadrStudio.Application.Automation.Agent.Tools;

internal static class AgentJsonSchemaValidator
{
    public static bool TryValidate(
        JsonElement value,
        JsonElement schema,
        out string error)
    {
        var errors = new List<string>();
        Validate(value, schema, "$", errors);
        error = errors.FirstOrDefault() ?? string.Empty;
        return errors.Count == 0;
    }

    private static void Validate(
        JsonElement value,
        JsonElement schema,
        string path,
        ICollection<string> errors)
    {
        if (errors.Count > 0 || schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("type", out var typeElement) &&
            typeElement.ValueKind == JsonValueKind.String &&
            !MatchesType(value, typeElement.GetString()))
        {
            errors.Add($"{path} must be {typeElement.GetString()}.");
            return;
        }

        if (schema.TryGetProperty("enum", out var enumElement) &&
            enumElement.ValueKind == JsonValueKind.Array &&
            !enumElement.EnumerateArray().Any(candidate =>
                JsonElement.DeepEquals(candidate, value)))
        {
            errors.Add($"{path} is not one of the allowed values.");
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                ValidateObject(value, schema, path, errors);
                break;
            case JsonValueKind.Array:
                ValidateArray(value, schema, path, errors);
                break;
            case JsonValueKind.String:
                ValidateString(value, schema, path, errors);
                break;
            case JsonValueKind.Number:
                ValidateNumber(value, schema, path, errors);
                break;
        }
    }

    private static void ValidateObject(
        JsonElement value,
        JsonElement schema,
        string path,
        ICollection<string> errors)
    {
        var properties = schema.TryGetProperty("properties", out var declared) &&
                         declared.ValueKind == JsonValueKind.Object
            ? declared
            : default;

        if (schema.TryGetProperty("required", out var required) &&
            required.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in required.EnumerateArray())
            {
                var name = item.GetString();
                if (!string.IsNullOrWhiteSpace(name) && !value.TryGetProperty(name, out _))
                {
                    errors.Add($"{path}.{name} is required.");
                    return;
                }
            }
        }

        var additionalAllowed =
            !schema.TryGetProperty("additionalProperties", out var additional) ||
            additional.ValueKind != JsonValueKind.False;
        foreach (var property in value.EnumerateObject())
        {
            if (properties.ValueKind == JsonValueKind.Object &&
                properties.TryGetProperty(property.Name, out var propertySchema))
            {
                Validate(property.Value, propertySchema, $"{path}.{property.Name}", errors);
                if (errors.Count > 0) return;
            }
            else if (!additionalAllowed)
            {
                errors.Add($"{path}.{property.Name} is not allowed.");
                return;
            }
        }
    }

    private static void ValidateArray(
        JsonElement value,
        JsonElement schema,
        string path,
        ICollection<string> errors)
    {
        var count = value.GetArrayLength();
        if (TryReadInt(schema, "minItems", out var minimum) && count < minimum)
        {
            errors.Add($"{path} must contain at least {minimum} item(s).");
            return;
        }
        if (TryReadInt(schema, "maxItems", out var maximum) && count > maximum)
        {
            errors.Add($"{path} cannot contain more than {maximum} item(s).");
            return;
        }
        if (!schema.TryGetProperty("items", out var itemSchema)) return;

        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            Validate(item, itemSchema, $"{path}[{index++}]", errors);
            if (errors.Count > 0) return;
        }
    }

    private static void ValidateString(
        JsonElement value,
        JsonElement schema,
        string path,
        ICollection<string> errors)
    {
        var text = value.GetString() ?? string.Empty;
        if (TryReadInt(schema, "maxLength", out var maximum) && text.Length > maximum)
        {
            errors.Add($"{path} cannot exceed {maximum} characters.");
            return;
        }
        if (schema.TryGetProperty("format", out var format) &&
            string.Equals(format.GetString(), "uuid", StringComparison.OrdinalIgnoreCase) &&
            !Guid.TryParse(text, out _))
        {
            errors.Add($"{path} must be a UUID.");
        }
    }

    private static void ValidateNumber(
        JsonElement value,
        JsonElement schema,
        string path,
        ICollection<string> errors)
    {
        if (!value.TryGetDouble(out var number) || !double.IsFinite(number))
        {
            errors.Add($"{path} must be a finite number.");
            return;
        }
        if (TryReadDouble(schema, "minimum", out var minimum) && number < minimum)
        {
            errors.Add($"{path} must be at least {minimum}.");
            return;
        }
        if (TryReadDouble(schema, "exclusiveMinimum", out var exclusiveMinimum) &&
            number <= exclusiveMinimum)
        {
            errors.Add($"{path} must be greater than {exclusiveMinimum}.");
            return;
        }
        if (TryReadDouble(schema, "maximum", out var maximum) && number > maximum)
        {
            errors.Add($"{path} cannot exceed {maximum}.");
        }
    }

    private static bool MatchesType(JsonElement value, string? type)
        => type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number &&
                         value.TryGetInt64(out _),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => true
        };

    private static bool TryReadInt(JsonElement element, string name, out int value)
    {
        value = default;
        return element.TryGetProperty(name, out var property) &&
               property.TryGetInt32(out value);
    }

    private static bool TryReadDouble(JsonElement element, string name, out double value)
    {
        value = default;
        return element.TryGetProperty(name, out var property) &&
               property.TryGetDouble(out value);
    }
}
