using System.Text;
using System.Text.Json;

namespace KadrStudio.Application.Automation.Agent.Tools;

public static class AgentActionApproval
{
    public static string CreateSignature(string toolName, JsonElement arguments)
        => toolName.Trim().ToLowerInvariant() + "\n" + CanonicalizeArguments(arguments);

    public static bool Matches(
        string expectedTool,
        JsonElement expectedArguments,
        string actualTool,
        JsonElement actualArguments)
        => string.Equals(expectedTool.Trim(), actualTool.Trim(), StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               CanonicalizeArguments(expectedArguments),
               CanonicalizeArguments(actualArguments),
               StringComparison.Ordinal);

    public static JsonElement NormalizeArguments(JsonElement arguments)
    {
        using var document = JsonDocument.Parse(CanonicalizeArguments(arguments));
        return document.RootElement.Clone();
    }

    private static string CanonicalizeArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return "{}";
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, arguments, omitReason: true);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element, bool omitReason)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .Where(property => !omitReason ||
                                                !property.Name.Equals("reason", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value, omitReason: false);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item, omitReason: false);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
