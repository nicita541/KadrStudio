using System.Globalization;
using System.Text.Json;

namespace KadrStudio.AiServer.Inference;

public static class StructuredOutputValidator
{
    public static bool TryCloseOpenContainers(
        string content,
        out string completedContent)
    {
        completedContent = string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var stack = new Stack<char>();
        var inString = false;
        var escaped = false;
        foreach (var character in content)
        {
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }
                continue;
            }

            switch (character)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                case '[':
                    stack.Push(character);
                    break;
                case '}':
                    if (stack.Count == 0 || stack.Pop() != '{') return false;
                    break;
                case ']':
                    if (stack.Count == 0 || stack.Pop() != '[') return false;
                    break;
            }
        }

        if (inString || escaped || stack.Count is 0 or > 64)
        {
            return false;
        }

        var trimmed = content.TrimEnd();
        if (trimmed.Length == 0 || trimmed[^1] is ':' or ',' or '{' or '[')
        {
            return false;
        }

        var suffix = new string(stack.Select(character => character == '{' ? '}' : ']').ToArray());
        completedContent = trimmed + suffix;
        try
        {
            using var _ = JsonDocument.Parse(completedContent);
            return true;
        }
        catch (JsonException)
        {
            completedContent = string.Empty;
            return false;
        }
    }

    public static bool TryValidate(
        string content,
        JsonElement schema,
        out string normalizedContent,
        out IReadOnlyList<string> errors)
    {
        normalizedContent = string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            errors = ["The model response is empty."];
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var failures = new List<string>();
            ValidateValue(document.RootElement, schema, "$", failures);
            if (failures.Count > 0)
            {
                errors = failures;
                return false;
            }

            normalizedContent = document.RootElement.GetRawText();
            errors = [];
            return true;
        }
        catch (JsonException exception)
        {
            errors = [$"Invalid JSON: {exception.Message}"];
            return false;
        }
    }

    private static void ValidateValue(
        JsonElement value,
        JsonElement schema,
        string path,
        List<string> errors)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}: schema must be an object.");
            return;
        }

        if (schema.TryGetProperty("enum", out var enumValues) &&
            enumValues.ValueKind == JsonValueKind.Array &&
            !enumValues.EnumerateArray().Any(candidate => JsonEquals(candidate, value)))
        {
            errors.Add($"{path}: value is not one of the allowed enum values.");
        }

        var expectedTypes = ReadExpectedTypes(schema);
        if (expectedTypes.Count > 0 && !expectedTypes.Any(type => MatchesType(value, type)))
        {
            errors.Add($"{path}: expected {string.Join(" or ", expectedTypes)}, got {value.ValueKind}.");
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
                ValidateString(value.GetString() ?? string.Empty, schema, path, errors);
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
        List<string> errors)
    {
        var properties = schema.TryGetProperty("properties", out var configuredProperties) &&
                         configuredProperties.ValueKind == JsonValueKind.Object
            ? configuredProperties
            : default;

        if (schema.TryGetProperty("required", out var required) &&
            required.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in required.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String &&
                    !value.TryGetProperty(item.GetString()!, out _))
                {
                    errors.Add($"{path}: required property '{item.GetString()}' is missing.");
                }
            }
        }

        var rejectAdditional = schema.TryGetProperty("additionalProperties", out var additional) &&
                               additional.ValueKind == JsonValueKind.False;
        foreach (var property in value.EnumerateObject())
        {
            if (properties.ValueKind == JsonValueKind.Object &&
                properties.TryGetProperty(property.Name, out var propertySchema))
            {
                ValidateValue(property.Value, propertySchema, $"{path}.{property.Name}", errors);
            }
            else if (rejectAdditional)
            {
                errors.Add($"{path}: additional property '{property.Name}' is not allowed.");
            }
        }
    }

    private static void ValidateArray(
        JsonElement value,
        JsonElement schema,
        string path,
        List<string> errors)
    {
        var items = value.EnumerateArray().ToArray();
        if (TryReadInteger(schema, "minItems", out var minItems) && items.Length < minItems)
        {
            errors.Add($"{path}: array contains fewer than {minItems} items.");
        }

        if (TryReadInteger(schema, "maxItems", out var maxItems) && items.Length > maxItems)
        {
            errors.Add($"{path}: array contains more than {maxItems} items.");
        }

        if (!schema.TryGetProperty("items", out var itemSchema) ||
            itemSchema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        for (var index = 0; index < items.Length; index++)
        {
            ValidateValue(items[index], itemSchema, $"{path}[{index}]", errors);
        }
    }

    private static void ValidateString(
        string value,
        JsonElement schema,
        string path,
        List<string> errors)
    {
        if (TryReadInteger(schema, "minLength", out var minLength) && value.Length < minLength)
        {
            errors.Add($"{path}: string is shorter than {minLength} characters.");
        }

        if (TryReadInteger(schema, "maxLength", out var maxLength) && value.Length > maxLength)
        {
            errors.Add($"{path}: string is longer than {maxLength} characters.");
        }

        if (schema.TryGetProperty("format", out var format) &&
            format.ValueKind == JsonValueKind.String &&
            string.Equals(format.GetString(), "uuid", StringComparison.OrdinalIgnoreCase) &&
            !Guid.TryParse(value, out _))
        {
            errors.Add($"{path}: value is not a valid UUID.");
        }
    }

    private static void ValidateNumber(
        JsonElement value,
        JsonElement schema,
        string path,
        List<string> errors)
    {
        if (!value.TryGetDouble(out var number))
        {
            errors.Add($"{path}: number cannot be represented.");
            return;
        }

        if (TryReadDouble(schema, "minimum", out var minimum) && number < minimum)
        {
            errors.Add($"{path}: number is below minimum {minimum.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (TryReadDouble(schema, "exclusiveMinimum", out var exclusiveMinimum) &&
            number <= exclusiveMinimum)
        {
            errors.Add($"{path}: number must be greater than {exclusiveMinimum.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (TryReadDouble(schema, "maximum", out var maximum) && number > maximum)
        {
            errors.Add($"{path}: number exceeds maximum {maximum.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (TryReadDouble(schema, "exclusiveMaximum", out var exclusiveMaximum) &&
            number >= exclusiveMaximum)
        {
            errors.Add($"{path}: number must be less than {exclusiveMaximum.ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    private static IReadOnlyList<string> ReadExpectedTypes(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out var type))
        {
            return [];
        }

        return type.ValueKind switch
        {
            JsonValueKind.String => [type.GetString()!],
            JsonValueKind.Array => type.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToArray(),
            _ => []
        };
    }

    private static bool MatchesType(JsonElement value, string type)
        => type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number &&
                         value.TryGetInt64(out _),
            _ => true
        };

    private static bool JsonEquals(JsonElement left, JsonElement right)
        => left.ValueKind == right.ValueKind && left.GetRawText() == right.GetRawText();

    private static bool TryReadInteger(JsonElement schema, string name, out int value)
    {
        value = 0;
        return schema.TryGetProperty(name, out var property) && property.TryGetInt32(out value);
    }

    private static bool TryReadDouble(JsonElement schema, string name, out double value)
    {
        value = 0;
        return schema.TryGetProperty(name, out var property) && property.TryGetDouble(out value);
    }
}
