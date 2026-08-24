using System.Text.Json;
using KadrStudio.Application.Automation.Agent;
using KadrStudio.Application.Automation.Agent.Tools;
using KadrStudio.Application.Automation.Agent.Tools.ReadOnly;

namespace KadrStudio.Core.Tests;

public sealed class AgentRangeQueryToolTests
{
    [Fact]
    public async Task Inspect_range_forwards_optional_semantic_query()
    {
        var backend = new CapturingBackend();
        var registry = AgentReadOnlyToolSet.Create(backend);
        var executor = new AgentToolExecutor(registry);
        var now = DateTimeOffset.UtcNow;
        var task = new AgentTaskState(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "test",
            AgentTaskPhase.Investigating, null, null, [], [], null, null, null, now, now);

        var arguments = AgentToolJson.ToElement(new
        {
            target_kind = "media",
            target_id = Guid.NewGuid(),
            start_seconds = 10,
            end_seconds = 20,
            detail = "frames",
            query = "Проверь, является ли этот блок заставкой."
        });

        var result = await executor.ExecuteAsync(
            task,
            AgentToolCall.Create(task.Id, "inspect_range", arguments));

        Assert.True(result.IsSuccess);
        Assert.NotNull(backend.LastRequest);
        Assert.Equal(
            "Проверь, является ли этот блок заставкой.",
            backend.LastRequest!.Query);
    }

    [Fact]
    public async Task All_reports_only_channels_actually_returned_by_backend()
    {
        var backend = new CapturingBackend
        {
            Result = AgentToolJson.ToElement(new
            {
                analyses = new[]
                {
                    new
                    {
                        status = "succeeded",
                        observation = new
                        {
                            analysis = new { summary = "Audio measured." },
                            vision = new
                            {
                                available = true,
                                coverage_complete = true,
                                observations = new[]
                                {
                                    new { start_seconds = 0, end_seconds = 10 }
                                }
                            }
                        }
                    }
                }
            })
        };
        var registry = AgentReadOnlyToolSet.Create(backend);
        var executor = new AgentToolExecutor(registry);
        var now = DateTimeOffset.UtcNow;
        var task = new AgentTaskState(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "test",
            AgentTaskPhase.Investigating, null, null, [], [], null, null, null, now, now);

        var result = await executor.ExecuteAsync(
            task,
            AgentToolCall.Create(task.Id, "inspect_range", AgentToolJson.ToElement(new
            {
                target_kind = "sequence",
                target_id = task.SourceSequenceId,
                start_seconds = 0,
                end_seconds = 10,
                detail = "all",
                query = "Measure the requested semantic evidence."
            })));

        Assert.True(result.IsSuccess);
        Assert.True(result.EvidenceCapabilities.HasFlag(AgentEvidenceCapabilities.Frames));
        Assert.True(result.EvidenceCapabilities.HasFlag(AgentEvidenceCapabilities.Audio));
        Assert.False(result.EvidenceCapabilities.HasFlag(AgentEvidenceCapabilities.Transcript));
    }

    [Fact]
    public async Task Direct_media_observation_reports_the_same_typed_channels()
    {
        var backend = new CapturingBackend
        {
            Result = AgentToolJson.ToElement(new
            {
                observation = new
                {
                    analysis = new { summary = "Audio measured." },
                    vision = new
                    {
                        available = true,
                        coverage_complete = true,
                        observations = new[] { new { start_seconds = 0, end_seconds = 10 } }
                    },
                    transcript = new { engine = "whisper", cues = Array.Empty<object>() }
                }
            })
        };
        var registry = AgentReadOnlyToolSet.Create(backend);
        var executor = new AgentToolExecutor(registry);
        var now = DateTimeOffset.UtcNow;
        var task = new AgentTaskState(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "test",
            AgentTaskPhase.Investigating, null, null, [], [], null, null, null, now, now);

        var result = await executor.ExecuteAsync(
            task,
            AgentToolCall.Create(task.Id, "inspect_range", AgentToolJson.ToElement(new
            {
                target_kind = "media",
                target_id = Guid.NewGuid(),
                start_seconds = 0,
                end_seconds = 10,
                detail = "all"
            })));

        Assert.True(result.IsSuccess);
        Assert.True(result.EvidenceCapabilities.HasFlag(AgentEvidenceCapabilities.Frames));
        Assert.True(result.EvidenceCapabilities.HasFlag(AgentEvidenceCapabilities.Audio));
        Assert.True(result.EvidenceCapabilities.HasFlag(AgentEvidenceCapabilities.Transcript));
    }

    [Fact]
    public async Task Technical_video_analysis_does_not_claim_audio_for_a_silent_source()
    {
        var backend = new CapturingBackend
        {
            Result = AgentToolJson.ToElement(new
            {
                observation = new
                {
                    analysis = new { summary = "Video geometry measured." },
                    audio_measurement_available = false
                }
            })
        };
        var registry = AgentReadOnlyToolSet.Create(backend);
        var executor = new AgentToolExecutor(registry);
        var now = DateTimeOffset.UtcNow;
        var task = new AgentTaskState(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "test",
            AgentTaskPhase.Investigating, null, null, [], [], null, null, null, now, now);

        var result = await executor.ExecuteAsync(
            task,
            AgentToolCall.Create(task.Id, "inspect_range", AgentToolJson.ToElement(new
            {
                target_kind = "media",
                target_id = Guid.NewGuid(),
                start_seconds = 0,
                end_seconds = 10,
                detail = "audio"
            })));

        Assert.True(result.IsSuccess);
        Assert.False(result.EvidenceCapabilities.HasFlag(AgentEvidenceCapabilities.Audio));
    }

    [Fact]
    public async Task Frames_capability_requires_complete_contact_sheet_coverage()
    {
        var backend = new CapturingBackend
        {
            Result = AgentToolJson.ToElement(new
            {
                analyses = new[]
                {
                    new
                    {
                        status = "succeeded",
                        observation = new
                        {
                            vision = new
                            {
                                available = true,
                                coverage_complete = false,
                                observations = new[]
                                {
                                    new { start_seconds = 0, end_seconds = 2 }
                                }
                            }
                        }
                    }
                }
            })
        };
        var registry = AgentReadOnlyToolSet.Create(backend);
        var executor = new AgentToolExecutor(registry);
        var now = DateTimeOffset.UtcNow;
        var task = new AgentTaskState(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "test",
            AgentTaskPhase.Investigating, null, null, [], [], null, null, null, now, now);

        var result = await executor.ExecuteAsync(
            task,
            AgentToolCall.Create(task.Id, "inspect_range", AgentToolJson.ToElement(new
            {
                target_kind = "sequence",
                target_id = task.SourceSequenceId,
                start_seconds = 0,
                end_seconds = 10,
                detail = "frames",
                query = "Observe the complete requested range."
            })));

        Assert.True(result.IsSuccess);
        Assert.False(result.EvidenceCapabilities.HasFlag(AgentEvidenceCapabilities.Frames));
    }

    [Fact]
    public async Task Content_inspection_without_query_runs_neutral_sensors()
    {
        var backend = new CapturingBackend();
        var registry = AgentReadOnlyToolSet.Create(backend);
        var executor = new AgentToolExecutor(registry);
        var now = DateTimeOffset.UtcNow;
        var task = new AgentTaskState(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "test",
            AgentTaskPhase.Investigating, null, null, [], [], null, null, null, now, now);

        var result = await executor.ExecuteAsync(
            task,
            AgentToolCall.Create(task.Id, "inspect_range", AgentToolJson.ToElement(new
            {
                target_kind = "sequence",
                target_id = task.SourceSequenceId,
                start_seconds = 0,
                end_seconds = 30,
                detail = "all"
            })));

        Assert.Equal(AgentToolResultStatus.Succeeded, result.Status);
        Assert.NotNull(backend.LastRequest);
        Assert.Equal(string.Empty, backend.LastRequest!.Query);
    }

    [Fact]
    public void Discovery_digest_keeps_time_distributed_tile_facts()
    {
        var description = string.Join(" ", Enumerable.Range(1, 8).Select(index =>
            $"Tile {index} @ {index * 5}s: fact-{index}.")) +
            " Visible sampled changes: measured change.";
        var data = AgentToolJson.ToElement(new
        {
            analyses = new[]
            {
                new
                {
                    observation = new
                    {
                        analysis = new { summary = "Technical facts." },
                        vision = new
                        {
                            observations = new[]
                            {
                                new
                                {
                                    start_seconds = 0,
                                    end_seconds = 40,
                                    description
                                }
                            }
                        }
                    }
                }
            }
        });

        var digest = AgentRangeEvidenceSummary.BuildDiscoveryDigest(
            "fallback",
            data);

        Assert.Contains("Tile 1 @ 5s: fact-1", digest);
        Assert.Contains("Tile 5 @ 25s: fact-5", digest);
        Assert.Contains("Tile 8 @ 40s: fact-8", digest);
        Assert.DoesNotContain("Tile 2 @ 10s: fact-2", digest);
    }

    [Fact]
    public void Range_summary_keeps_direct_media_facts_and_sequence_time_mapping()
    {
        var direct = AgentToolJson.ToElement(new
        {
            observation = new
            {
                vision = new
                {
                    observations = new[]
                    {
                        new
                        {
                            start_seconds = 10,
                            end_seconds = 20,
                            title = "Visible facts",
                            description = "Tile 1 @ 12s: title card."
                        }
                    }
                }
            }
        });
        var mapped = AgentToolJson.ToElement(new
        {
            analyses = new[]
            {
                new
                {
                    timeline_start_seconds = 100,
                    timeline_end_seconds = 110,
                    source_start_seconds = 10,
                    source_end_seconds = 20,
                    observation = direct.GetProperty("observation")
                }
            }
        });

        var directSummary = AgentRangeEvidenceSummary.Build("Direct.", direct);
        var mappedSummary = AgentRangeEvidenceSummary.Build("Mapped.", mapped);

        Assert.Contains("title card", directSummary);
        Assert.Contains("source 10-20s -> timeline 100-110s", mappedSummary);
        Assert.Contains("title card", mappedSummary);
    }

    private sealed class CapturingBackend : IAgentReadOnlyToolBackend
    {
        public AgentRangeInspectionRequest? LastRequest { get; private set; }

        public JsonElement Result { get; init; } = AgentToolJson.ToElement(new { ok = true });

        public ValueTask<JsonElement> InspectProjectAsync(
            AgentToolContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(AgentToolJson.EmptyObject());

        public ValueTask<JsonElement> InspectTimelineAsync(
            AgentToolContext context, Guid sequenceId, CancellationToken cancellationToken)
            => ValueTask.FromResult(AgentToolJson.EmptyObject());

        public ValueTask<JsonElement> InspectMediaAsync(
            AgentToolContext context, Guid mediaId, CancellationToken cancellationToken)
            => ValueTask.FromResult(AgentToolJson.EmptyObject());

        public ValueTask<JsonElement> InspectRangeAsync(
            AgentToolContext context,
            AgentRangeInspectionRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return ValueTask.FromResult(Result);
        }
    }
}
