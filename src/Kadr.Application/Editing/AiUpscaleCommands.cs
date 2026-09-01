using System.Collections.Immutable;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Editing;

public sealed record UpsertUpscaleJobCommand(UpscaleJob Job) : IEditCommand
{
    public string Description => "Обновить задание AnimeSR-X";

    public ProjectState Apply(ProjectState project)
    {
        if (Job.Id == Guid.Empty || Job.SequenceId == Guid.Empty || Job.Progress is < 0 or > 1)
            throw new EditRejectedException("Параметры задания апскейла некорректны.");
        if (project.FindSequence(Job.SequenceId) is null)
            throw new EditRejectedException("Последовательность задания апскейла не найдена.");
        return UpscaleSequenceCommand.Apply(project, Job.SequenceId, active =>
        {
            var jobs = active.UpscaleJobs.Any(item => item.Id == Job.Id)
                ? active.UpscaleJobs.Select(item => item.Id == Job.Id ? Job : item).ToImmutableArray()
                : active.UpscaleJobs.Add(Job);
            return active with { UpscaleJobs = jobs };
        });
    }
}

public sealed record PublishUpscaleRenditionCommand(
    IReadOnlyList<MediaSource> Sources,
    TimelineTrack UpscaledTrack,
    IReadOnlyList<MediaClip> Clips,
    TrackRenditionGroup Group) : IEditCommand
{
    public string Description => "Опубликовать дорожку AnimeSR-X";

    public ProjectState Apply(ProjectState project)
        => UpscaleSequenceCommand.Apply(project, Group.SequenceId, ApplyToActiveSequence);

    private ProjectState ApplyToActiveSequence(ProjectState project)
    {
        var active = project.ActiveSequence
            ?? throw new EditRejectedException("Активная последовательность не найдена.");
        if (Group.SequenceId != active.Id)
            throw new EditRejectedException("Результат апскейла относится к другой последовательности.");
        var originalTrack = project.FindTrack(Group.OriginalTrackId);
        if (originalTrack?.Kind != TrackKind.Visual)
            throw new EditRejectedException("Исходная видеодорожка апскейла не найдена.");
        if (UpscaledTrack.Id != Group.UpscaledTrackId || UpscaledTrack.Kind != TrackKind.Visual)
            throw new EditRejectedException("Производная дорожка апскейла некорректна.");
        if (Sources.Count == 0 || Clips.Count == 0)
            throw new EditRejectedException("AnimeSR-X не вернул обработанные клипы.");
        if (Sources.Any(source => source.Kind != MediaKind.Video || source.HasAudio))
            throw new EditRejectedException("Производные AnimeSR-X должны быть video-only файлами.");
        var sourceIds = Sources.Select(item => item.Id).ToHashSet();
        if (sourceIds.Count != Sources.Count || Sources.Any(item => project.Sources.ContainsKey(item.Id)))
            throw new EditRejectedException("Идентификатор производного медиа уже используется.");
        if (Clips.Any(clip => clip.TrackId != UpscaledTrack.Id || !sourceIds.Contains(clip.SourceId) || clip.Video is null))
            throw new EditRejectedException("Производные клипы не соответствуют дорожке AnimeSR-X.");

        var originalClips = project.MediaClips
            .Where(item => item.TrackId == originalTrack.Id && item.Video is not null)
            .OrderBy(item => item.Start)
            .ToArray();
        var enhancedClips = Clips.OrderBy(item => item.Start).ToArray();
        if (originalClips.Length != enhancedClips.Length || originalClips.Zip(enhancedClips).Any(pair =>
                pair.First.Start != pair.Second.Start || pair.First.Duration != pair.Second.Duration))
            throw new EditRejectedException("Временная геометрия производной дорожки не совпадает с исходной.");

        var previousGroups = project.RenditionGroups
            .Where(item => item.SequenceId == active.Id && item.OriginalTrackId == originalTrack.Id)
            .ToArray();
        var oldTrackIds = previousGroups.Select(item => item.UpscaledTrackId).ToHashSet();
        var oldClipSourceIds = project.MediaClips
            .Where(item => oldTrackIds.Contains(item.TrackId))
            .Select(item => item.SourceId)
            .ToHashSet();
        var sourcesUsedByOtherSequences = project.Sequences
            .Where(item => item.Id != active.Id)
            .SelectMany(item => item.MediaClips)
            .Select(item => item.SourceId)
            .ToHashSet();
        var removableSourceIds = oldClipSourceIds.Where(item => !sourcesUsedByOtherSequences.Contains(item));
        var sources = project.Sources.RemoveRange(removableSourceIds);
        foreach (var source in Sources) sources = sources.Add(source.Id, source);

        var tracks = project.Tracks
            .Where(item => !oldTrackIds.Contains(item.Id))
            .Select(item => item.Id == originalTrack.Id ? item with { IsVisible = true } : item)
            .Append(UpscaledTrack with { IsVisible = false })
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Index)
            .ToImmutableArray();
        var clips = project.MediaClips
            .Where(item => !oldTrackIds.Contains(item.TrackId))
            .Concat(Clips)
            .ToImmutableArray();
        return project with
        {
            Sources = sources,
            Tracks = tracks,
            MediaClips = clips,
            RenditionGroups = project.RenditionGroups
                .Where(item => !(item.SequenceId == active.Id && item.OriginalTrackId == originalTrack.Id))
                .Append(Group with
                {
                    ActiveRendition = TrackRenditionKind.Original,
                    IsStale = false,
                    UpdatedAt = DateTimeOffset.UtcNow
                })
                .ToImmutableArray()
        };
    }
}

internal static class UpscaleSequenceCommand
{
    public static ProjectState Apply(
        ProjectState project,
        Guid sequenceId,
        Func<ProjectState, ProjectState> applyToActiveSequence)
    {
        var originalActiveSequenceId = project.ActiveSequenceId
            ?? throw new EditRejectedException("Активная последовательность не найдена.");
        if (project.FindSequence(sequenceId) is null)
            throw new EditRejectedException("Последовательность апскейла не найдена.");

        if (originalActiveSequenceId == sequenceId)
            return applyToActiveSequence(project);

        var target = project.ActivateSequence(sequenceId);
        var updatedTarget = applyToActiveSequence(target).SynchronizeActiveSequence();
        return updatedTarget.ActivateSequence(originalActiveSequenceId);
    }
}

public sealed record SetTrackRenditionCommand(Guid GroupId, TrackRenditionKind Rendition) : IEditCommand
{
    public string Description => Rendition == TrackRenditionKind.Original
        ? "Показать оригинальную дорожку"
        : "Показать дорожку AnimeSR-X";

    public ProjectState Apply(ProjectState project)
    {
        var group = project.RenditionGroups.FirstOrDefault(item => item.Id == GroupId)
            ?? throw new EditRejectedException("Связь оригинала и апскейла не найдена.");
        if (Rendition == TrackRenditionKind.Upscaled && group.IsStale)
            throw new EditRejectedException("Апскейл устарел после изменения исходной дорожки. Запустите обработку повторно.");
        var original = project.FindTrack(group.OriginalTrackId)
            ?? throw new EditRejectedException("Оригинальная дорожка не найдена.");
        var upscaled = project.FindTrack(group.UpscaledTrackId)
            ?? throw new EditRejectedException("Дорожка апскейла не найдена.");
        return project with
        {
            Tracks = project.Tracks.Select(track => track.Id switch
            {
                var id when id == original.Id => track with { IsVisible = Rendition == TrackRenditionKind.Original },
                var id when id == upscaled.Id => track with { IsVisible = Rendition == TrackRenditionKind.Upscaled },
                _ => track
            }).ToImmutableArray(),
            RenditionGroups = project.RenditionGroups.Select(item => item.Id == group.Id
                ? item with { ActiveRendition = Rendition, UpdatedAt = DateTimeOffset.UtcNow }
                : item).ToImmutableArray()
        };
    }
}
