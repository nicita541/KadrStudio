using System.Text.Json;

namespace KadrStudio.Application.Automation.Agent.Tools.ReadOnly;

public enum AgentRangeTargetKind
{
    Media,
    Sequence
}

public enum AgentRangeInspectionDetail
{
    Summary,
    Frames,
    Audio,
    Transcript,
    All
}

public sealed record AgentRangeInspectionRequest(
    AgentRangeTargetKind TargetKind,
    Guid TargetId,
    double StartSeconds,
    double EndSeconds,
    AgentRangeInspectionDetail Detail,
    string Query = "");

internal static class AgentRangeEvidenceCapabilities
{
    public static AgentEvidenceCapabilities From(
        AgentRangeInspectionDetail detail,
        JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("analysis_deferred", out var deferred) &&
            deferred.ValueKind == JsonValueKind.True)
        {
            return AgentEvidenceCapabilities.None;
        }

        if (detail == AgentRangeInspectionDetail.Summary)
        {
            return AgentEvidenceCapabilities.Timeline;
        }

        var capabilities = AgentEvidenceCapabilities.None;
        var hasAudioMeasurement = false;
        var hasFrames = false;
        var hasTranscript = false;

        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("observation", out var directObservation) &&
            directObservation.ValueKind == JsonValueKind.Object)
        {
            hasFrames = HasAvailableVision(directObservation);
            hasAudioMeasurement = HasAvailableAudioMeasurement(directObservation);
            hasTranscript = HasAvailableTranscript(directObservation);
        }

        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("analyses", out var analyses) &&
            analyses.ValueKind == JsonValueKind.Array)
        {
            foreach (var analysis in analyses.EnumerateArray())
            {
                if (analysis.ValueKind != JsonValueKind.Object ||
                    !analysis.TryGetProperty("status", out var status) ||
                    status.ValueKind != JsonValueKind.String ||
                    !string.Equals(status.GetString(), "succeeded", StringComparison.OrdinalIgnoreCase) ||
                    !analysis.TryGetProperty("observation", out var observation) ||
                    observation.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                hasFrames |= HasAvailableVision(observation);
                hasAudioMeasurement |= HasAvailableAudioMeasurement(observation);
                hasTranscript |= HasAvailableTranscript(observation);
            }
        }

        if (detail is AgentRangeInspectionDetail.Frames or AgentRangeInspectionDetail.All &&
            hasFrames)
        {
            capabilities |= AgentEvidenceCapabilities.Frames;
        }

        if (detail is AgentRangeInspectionDetail.Frames or
                AgentRangeInspectionDetail.Audio or
                AgentRangeInspectionDetail.All &&
            hasAudioMeasurement)
        {
            capabilities |= AgentEvidenceCapabilities.Audio;
        }

        if (detail is AgentRangeInspectionDetail.Transcript or AgentRangeInspectionDetail.All &&
            hasTranscript)
        {
            capabilities |= AgentEvidenceCapabilities.Transcript;
        }

        return capabilities;
    }

    private static bool HasAvailableVision(JsonElement observation)
        => observation.TryGetProperty("vision", out var vision) &&
           vision.ValueKind == JsonValueKind.Object &&
           vision.TryGetProperty("available", out var available) &&
           available.ValueKind == JsonValueKind.True &&
           vision.TryGetProperty("coverage_complete", out var coverageComplete) &&
           coverageComplete.ValueKind == JsonValueKind.True &&
           vision.TryGetProperty("observations", out var observations) &&
           observations.ValueKind == JsonValueKind.Array &&
           observations.GetArrayLength() > 0;

    private static bool HasAvailableAudioMeasurement(JsonElement observation)
    {
        if (observation.TryGetProperty("audio_measurement_available", out var available))
        {
            return available.ValueKind == JsonValueKind.True;
        }

        return observation.TryGetProperty("analysis", out var analysis) &&
               analysis.ValueKind == JsonValueKind.Object;
    }

    private static bool HasAvailableTranscript(JsonElement observation)
        => observation.TryGetProperty("transcript", out var transcript) &&
           transcript.ValueKind == JsonValueKind.Object &&
           transcript.TryGetProperty("engine", out var engine) &&
           engine.ValueKind == JsonValueKind.String &&
           !string.IsNullOrWhiteSpace(engine.GetString());
}
