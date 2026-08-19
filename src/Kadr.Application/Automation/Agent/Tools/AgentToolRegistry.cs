using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace KadrStudio.Application.Automation.Agent.Tools;

public sealed partial class AgentToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _tools =
        new(StringComparer.OrdinalIgnoreCase);

    public ImmutableArray<AgentToolDescriptor> Descriptors =>
        _tools.Values
            .Select(tool => tool.Descriptor)
            .OrderBy(descriptor => descriptor.Name, StringComparer.Ordinal)
            .ToImmutableArray();

    public void Register(IAgentTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        ValidateDescriptor(tool.Descriptor);

        if (!_tools.TryAdd(tool.Descriptor.Name, tool))
        {
            throw new InvalidOperationException(
                $"Agent tool '{tool.Descriptor.Name}' is already registered.");
        }
    }

    public bool TryGet(
        string name,
        out IAgentTool? tool)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            tool = null;
            return false;
        }

        return _tools.TryGetValue(name.Trim(), out tool);
    }

    private static void ValidateDescriptor(AgentToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (string.IsNullOrWhiteSpace(descriptor.Name) ||
            !ToolNamePattern().IsMatch(descriptor.Name))
        {
            throw new ArgumentException(
                "Tool name must use lower snake_case and contain only a-z, 0-9 and underscore.",
                nameof(descriptor));
        }

        if (string.IsNullOrWhiteSpace(descriptor.Description))
        {
            throw new ArgumentException(
                "Tool description cannot be empty.",
                nameof(descriptor));
        }

        if (descriptor.InputSchema.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            throw new ArgumentException(
                "Tool input schema must be a JSON object.",
                nameof(descriptor));
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ToolNamePattern();
}
