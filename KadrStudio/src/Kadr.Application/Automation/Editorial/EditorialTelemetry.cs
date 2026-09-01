using System.Collections.Immutable;

namespace KadrStudio.Application.Automation.Editorial;

public sealed record EditorialTelemetryEvent(
    DateTimeOffset Timestamp,
    Guid TaskId,
    string Metric,
    double Value,
    ImmutableDictionary<string, string> Dimensions);

public interface IEditorialTelemetrySink
{
    void Record(EditorialTelemetryEvent telemetryEvent);
}

public sealed class NullEditorialTelemetrySink : IEditorialTelemetrySink
{
    public static NullEditorialTelemetrySink Instance { get; } = new();

    private NullEditorialTelemetrySink()
    {
    }

    public void Record(EditorialTelemetryEvent telemetryEvent)
    {
    }
}
