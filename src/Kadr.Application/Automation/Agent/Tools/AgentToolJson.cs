using System.Text.Json;

namespace KadrStudio.Application.Automation.Agent.Tools;

public static class AgentToolJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static JsonElement EmptyObject()
        => ParseObject("{}");

    public static JsonElement ParseObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("JSON cannot be empty.", nameof(json));
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "JSON root must be an object.",
                nameof(json));
        }

        return document.RootElement.Clone();
    }

    public static JsonElement ToElement<T>(T value)
        => JsonSerializer.SerializeToElement(value, SerializerOptions);

    public static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new AgentToolInputException(
                "Tool arguments must be a JSON object.");
        }
    }

    public static void EnsureOnlyProperties(
        JsonElement value,
        params string[] allowedProperties)
    {
        RequireObject(value);

        var allowed = new HashSet<string>(
            allowedProperties,
            StringComparer.Ordinal);

        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new AgentToolInputException(
                    $"Unknown tool argument '{property.Name}'.");
            }
        }
    }

    public static Guid RequireGuid(
        JsonElement value,
        string propertyName)
    {
        RequireObject(value);

        if (!value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(property.GetString(), out var parsed) ||
            parsed == Guid.Empty)
        {
            throw new AgentToolInputException(
                $"'{propertyName}' must be a non-empty GUID string.");
        }

        return parsed;
    }

    public static Guid? OptionalGuid(
        JsonElement value,
        string propertyName)
    {
        RequireObject(value);

        if (!value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(property.GetString(), out var parsed) ||
            parsed == Guid.Empty)
        {
            throw new AgentToolInputException(
                $"'{propertyName}' must be a non-empty GUID string when provided.");
        }

        return parsed;
    }

    public static string RequireString(
        JsonElement value,
        string propertyName)
    {
        RequireObject(value);

        if (!value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new AgentToolInputException(
                $"'{propertyName}' must be a non-empty string.");
        }

        return property.GetString()!.Trim();
    }

    public static string? OptionalString(
        JsonElement value,
        string propertyName)
    {
        RequireObject(value);

        if (!value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new AgentToolInputException(
                $"'{propertyName}' must be a string when provided.");
        }

        var result = property.GetString()?.Trim();
        return string.IsNullOrEmpty(result) ? null : result;
    }


    public static bool OptionalBoolean(
        JsonElement value,
        string propertyName,
        bool defaultValue = false)
    {
        RequireObject(value);

        if (!value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return defaultValue;
        }

        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new AgentToolInputException(
                $"'{propertyName}' must be a boolean when provided.");
        }

        return property.GetBoolean();
    }

    public static IReadOnlyList<Guid> RequireGuidArray(
        JsonElement value,
        string propertyName,
        int maximumCount = 100)
    {
        RequireObject(value);

        if (!value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            throw new AgentToolInputException(
                $"'{propertyName}' must be an array of GUID strings.");
        }

        var result = new List<Guid>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(item.GetString(), out var parsed) ||
                parsed == Guid.Empty)
            {
                throw new AgentToolInputException(
                    $"'{propertyName}' must contain only non-empty GUID strings.");
            }

            if (!result.Contains(parsed))
            {
                result.Add(parsed);
            }

            if (result.Count > maximumCount)
            {
                throw new AgentToolInputException(
                    $"'{propertyName}' contains too many values.");
            }
        }

        if (result.Count == 0)
        {
            throw new AgentToolInputException(
                $"'{propertyName}' must contain at least one GUID.");
        }

        return result;
    }

    public static double RequireFiniteDouble(
        JsonElement value,
        string propertyName,
        double minimum = double.NegativeInfinity)
    {
        RequireObject(value);

        if (!value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetDouble(out var parsed) ||
            double.IsNaN(parsed) ||
            double.IsInfinity(parsed) ||
            parsed < minimum)
        {
            throw new AgentToolInputException(
                $"'{propertyName}' must be a finite number greater than or equal to {minimum}.");
        }

        return parsed;
    }
}
