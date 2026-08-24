using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KadrStudio.Application.Automation.Agent.Tools.ReadOnly;

/// <summary>
/// Builds a compact, model-facing account of measured range evidence. Raw
/// backend payloads remain available for audit, while the summary preserves
/// measured observations and their ranges when prompt retention has to omit
/// a very large JSON payload.
/// </summary>
public static class AgentRangeEvidenceSummary
{
    private const int MaximumCharacters = 16_000;
    private const int MaximumSegmentsPerChannel = 12;
    private static readonly Regex TileFactPattern = new(
        @"Tile\s+\d+\s+@\s+.*?(?=(?:\s+Tile\s+\d+\s+@)|(?:\s+Visible sampled changes:)|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    public static string Build(string prefix, JsonElement data)
    {
        var builder = new StringBuilder(prefix.Trim());
        if (data.ValueKind != JsonValueKind.Object)
        {
            return builder.ToString();
        }

        if (TryGetObject(data, "observation", out var directObservation))
        {
            AppendObservation(builder, directObservation);
            return Compact(builder.ToString(), MaximumCharacters);
        }
        if (!data.TryGetProperty("analyses", out var analyses) ||
            analyses.ValueKind != JsonValueKind.Array)
        {
            return builder.ToString();
        }

        foreach (var item in analyses.EnumerateArray())
        {
            if (!TryGetObject(item, "observation", out var observation))
            {
                continue;
            }

            AppendTimelineMapping(builder, item);
            AppendObservation(builder, observation);

            if (builder.Length >= MaximumCharacters)
            {
                break;
            }
        }

        if (builder.Length <= MaximumCharacters)
        {
            return builder.ToString();
        }

        return builder.ToString(0, MaximumCharacters - 1).TrimEnd() + "…";
    }

    public static string BuildDiscoveryDigest(
        string fallbackSummary,
        JsonElement? data,
        int maximumCharacters = 4_500)
    {
        if (data is not { ValueKind: JsonValueKind.Object } root)
        {
            return Compact(fallbackSummary, maximumCharacters);
        }

        var builder = new StringBuilder();
        if (TryGetObject(root, "observation", out var directObservation))
        {
            AppendDiscoveryObservation(builder, directObservation);
            return builder.Length == 0
                ? Compact(fallbackSummary, maximumCharacters)
                : Compact(builder.ToString(), maximumCharacters);
        }
        if (!root.TryGetProperty("analyses", out var analyses) ||
            analyses.ValueKind != JsonValueKind.Array)
        {
            return Compact(fallbackSummary, maximumCharacters);
        }

        foreach (var analysis in analyses.EnumerateArray())
        {
            if (!TryGetObject(analysis, "observation", out var observation))
            {
                continue;
            }
            AppendTimelineMapping(builder, analysis);
            AppendDiscoveryObservation(builder, observation);
        }

        return builder.Length == 0
            ? Compact(fallbackSummary, maximumCharacters)
            : Compact(builder.ToString(), maximumCharacters);
    }

    private static void AppendObservation(StringBuilder builder, JsonElement observation)
    {
        AppendSummary(builder, observation, "analysis", "Measurement");
        AppendAudio(builder, observation);
        AppendSegments(builder, observation, "vision", "observations", "Vision segment");
        AppendTranscript(builder, observation);
    }

    private static void AppendDiscoveryObservation(
        StringBuilder builder,
        JsonElement observation)
    {
        AppendSummary(builder, observation, "analysis", "Measurement");
        if (!TryGetObject(observation, "vision", out var vision) ||
            !vision.TryGetProperty("observations", out var segments) ||
            segments.ValueKind != JsonValueKind.Array)
        {
            AppendAudio(builder, observation);
            return;
        }

        foreach (var segment in segments.EnumerateArray())
        {
            var start = ReadNumber(segment, "start_seconds");
            var end = ReadNumber(segment, "end_seconds");
            var description = ReadString(segment, "description");
            var facts = TileFactPattern.Matches(description)
                .Select(match => match.Value.Trim())
                .ToArray();
            if (facts.Length == 0)
            {
                Append(builder, $"Sample {FormatRange(start, end)}: {Compact(description, 500)}");
                continue;
            }

            // Four evenly spread facts keep the maximum OCR sampling gap below
            // the region indexer's separation threshold for an eight-tile sheet.
            // Three facts left ~20s holes and split one continuous credit block.
            var selected = new[]
                {
                    0,
                    (facts.Length - 1) / 3,
                    (facts.Length - 1) * 2 / 3,
                    facts.Length - 1
                }
                .Distinct()
                .Select(index => Compact(facts[index], 280));
            Append(
                builder,
                $"Sample {FormatRange(start, end)}: {string.Join(" | ", selected)}");
        }

        // OCR/visual facts choose the next exact probes, so preserve every
        // independently measured sheet before the longer loudness list. Audio
        // remains present after them and is measured again in detailed probes.
        AppendAudio(builder, observation);
    }

    private static void AppendTimelineMapping(StringBuilder builder, JsonElement analysis)
    {
        var timelineStart = ReadNumber(analysis, "timeline_start_seconds");
        var timelineEnd = ReadNumber(analysis, "timeline_end_seconds");
        var sourceStart = ReadNumber(analysis, "source_start_seconds");
        var sourceEnd = ReadNumber(analysis, "source_end_seconds");
        if (timelineStart is null || timelineEnd is null ||
            sourceStart is null || sourceEnd is null)
        {
            return;
        }

        Append(
            builder,
            $"Time mapping: source {FormatRange(sourceStart, sourceEnd)} -> " +
            $"timeline {FormatRange(timelineStart, timelineEnd)}.");
    }

    private static void AppendSummary(
        StringBuilder builder,
        JsonElement observation,
        string propertyName,
        string label)
    {
        if (!TryGetObject(observation, propertyName, out var channel) ||
            !channel.TryGetProperty("summary", out var summary) ||
            summary.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(summary.GetString()))
        {
            return;
        }

        Append(builder, $"{label}: {summary.GetString()!.Trim()}");
    }

    private static void AppendSegments(
        StringBuilder builder,
        JsonElement observation,
        string channelName,
        string collectionName,
        string label)
    {
        if (!TryGetObject(observation, channelName, out var channel) ||
            !channel.TryGetProperty(collectionName, out var segments) ||
            segments.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var segment in segments.EnumerateArray().Take(MaximumSegmentsPerChannel))
        {
            var start = ReadNumber(segment, "start_seconds");
            var end = ReadNumber(segment, "end_seconds");
            var title = ReadString(segment, "title");
            var description = ReadString(segment, "description");
            Append(
                builder,
                $"{label} {FormatRange(start, end)}: {title}{FormatDescription(description)}");
        }
    }

    private static void AppendTranscript(StringBuilder builder, JsonElement observation)
    {
        if (!TryGetObject(observation, "transcript", out var transcript))
        {
            return;
        }

        var engine = ReadString(transcript, "engine");
        var warning = ReadString(transcript, "warning");
        if (!string.IsNullOrWhiteSpace(engine) || !string.IsNullOrWhiteSpace(warning))
        {
            Append(
                builder,
                $"Transcript: {engine}{(string.IsNullOrWhiteSpace(warning) ? string.Empty : $"; warning={warning}")}");
        }

        if (!transcript.TryGetProperty("cues", out var cues) ||
            cues.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var cue in cues.EnumerateArray().Take(MaximumSegmentsPerChannel))
        {
            Append(
                builder,
                $"Transcript cue {FormatRange(ReadNumber(cue, "start_seconds"), ReadNumber(cue, "end_seconds"))}: {ReadString(cue, "text")}");
        }
    }

    private static void AppendAudio(StringBuilder builder, JsonElement observation)
    {
        if (!TryGetObject(observation, "analysis", out var analysis))
        {
            return;
        }

        if (analysis.TryGetProperty("loudness_change_points", out var changes) &&
            changes.ValueKind == JsonValueKind.Array)
        {
            foreach (var change in changes.EnumerateArray().Take(MaximumSegmentsPerChannel))
            {
                Append(
                    builder,
                    $"Audio loudness change {FormatRange(ReadNumber(change, "start_seconds"), ReadNumber(change, "end_seconds"))}: " +
                    $"momentary={ReadNumber(change, "momentary_lufs"):0.##} LUFS, " +
                    $"short={ReadNumber(change, "short_term_lufs"):0.##} LUFS, " +
                    $"delta={ReadNumber(change, "delta_from_previous_lufs"):+0.##;-0.##;0} LUFS.");
            }
        }

        if (!analysis.TryGetProperty("loudness_windows", out var windows) ||
            windows.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var measured = windows.EnumerateArray().ToArray();
        foreach (var index in new[] { 0, measured.Length / 2, measured.Length - 1 }
                     .Where(index => index >= 0 && index < measured.Length)
                     .Distinct())
        {
            var window = measured[index];
            Append(
                builder,
                $"Audio sample {FormatRange(ReadNumber(window, "start_seconds"), ReadNumber(window, "end_seconds"))}: " +
                $"momentary={ReadNumber(window, "momentary_lufs"):0.##} LUFS, " +
                $"short={ReadNumber(window, "short_term_lufs"):0.##} LUFS.");
        }
    }

    private static void Append(StringBuilder builder, string value)
    {
        if (builder.Length >= MaximumCharacters || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.AppendLine();
        builder.Append(value.Trim());
    }

    private static bool TryGetObject(
        JsonElement parent,
        string propertyName,
        out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(propertyName, out value) &&
            value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string ReadString(JsonElement value, string propertyName)
        => value.ValueKind == JsonValueKind.Object &&
           value.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static double? ReadNumber(JsonElement value, string propertyName)
        => value.ValueKind == JsonValueKind.Object &&
           value.TryGetProperty(propertyName, out var property) &&
           property.TryGetDouble(out var result)
            ? result
            : null;

    private static string FormatRange(double? start, double? end)
        => start is not null && end is not null
            ? $"{start:0.###}-{end:0.###}s"
            : "at an unspecified range";

    private static string FormatDescription(string description)
        => string.IsNullOrWhiteSpace(description)
            ? string.Empty
            : $" — {description}";

    private static string Compact(string? value, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized[..(maximumCharacters - 1)].TrimEnd() + "…";
    }
}
