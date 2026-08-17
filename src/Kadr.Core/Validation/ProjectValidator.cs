using KadrStudio.Core.Domain;

namespace KadrStudio.Core.Validation;

public sealed record ValidationError(string Code, string Message, Guid? EntityId = null);

public sealed record ValidationResult(IReadOnlyList<ValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;
    public static ValidationResult Valid { get; } = new(Array.Empty<ValidationError>());
}

public interface IProjectValidator
{
    ValidationResult Validate(ProjectState project);
}

public sealed class ProjectValidator : IProjectValidator
{
    public ValidationResult Validate(ProjectState project)
    {
        var errors = new List<ValidationError>();
        ValidateProject(project, errors);
        ValidateTracks(project, errors);
        ValidateSources(project, errors);
        ValidateClips(project, errors);
        ValidateText(project, errors);
        ValidateTransitions(project, errors);
        ValidateMarkers(project, errors);
        ValidateInOut(project, errors);
        ValidateSequenceWorkspace(project, errors);
        ValidateSourceAnnotations(project, errors);
        ValidateMontagePlans(project, errors);
        return errors.Count == 0 ? ValidationResult.Valid : new ValidationResult(errors);
    }

    private static void ValidateProject(ProjectState project, ICollection<ValidationError> errors)
    {
        if (project.Id == Guid.Empty) errors.Add(new("project.id", "Project ID cannot be empty."));
        if (string.IsNullOrWhiteSpace(project.Name)) errors.Add(new("project.name", "Project name cannot be empty."));
        if (project.CanvasWidth is < 320 or > 7680 || project.CanvasHeight is < 240 or > 4320)
            errors.Add(new("project.canvas", "Canvas size is outside the supported range."));
        if (project.Sequence.AudioSampleRate is < 8_000 or > 192_000)
            errors.Add(new("project.sample-rate", "Audio sample rate is outside the supported range."));
        if (project.FrameRate.FramesPerSecond is < 1 or > 120)
            errors.Add(new("project.frame-rate", "Frame rate is outside the supported range."));
    }

    private static void ValidateTracks(ProjectState project, ICollection<ValidationError> errors)
    {
        foreach (var duplicate in project.Tracks.GroupBy(item => item.Id).Where(group => group.Count() > 1))
            errors.Add(new("track.duplicate-id", "Track IDs must be unique.", duplicate.Key));
        foreach (var duplicate in project.Tracks.GroupBy(item => (item.Kind, item.Index)).Where(group => group.Count() > 1))
            errors.Add(new("track.duplicate-index", $"Track {duplicate.Key.Kind}{duplicate.Key.Index + 1} is duplicated."));
        foreach (var track in project.Tracks)
        {
            if (track.Id == Guid.Empty) errors.Add(new("track.id", "Track ID cannot be empty."));
            if (track.Index < 0) errors.Add(new("track.index", "Track index cannot be negative.", track.Id));
            if (string.IsNullOrWhiteSpace(track.Name)) errors.Add(new("track.name", "Track name cannot be empty.", track.Id));
        }
        if (project.Tracks.Count(item => item.Kind == TrackKind.Visual) < 2)
            errors.Add(new("track.visual-minimum", "A project must contain at least two visual tracks."));
        if (project.Tracks.Count(item => item.Kind == TrackKind.Audio) < 2)
            errors.Add(new("track.audio-minimum", "A project must contain at least two audio tracks."));
    }

    private static void ValidateSources(ProjectState project, ICollection<ValidationError> errors)
    {
        foreach (var pair in project.Sources)
        {
            var source = pair.Value;
            if (pair.Key != source.Id || source.Id == Guid.Empty)
                errors.Add(new("source.id", "Media source dictionary key and ID must match.", source.Id));
            if (string.IsNullOrWhiteSpace(source.Path)) errors.Add(new("source.path", "Media source path cannot be empty.", source.Id));
            if (source.Duration <= TimelineTime.Zero) errors.Add(new("source.duration", "Media source duration must be positive.", source.Id));
            if (!source.Streams.IsDefault)
            {
                foreach (var stream in source.Streams)
                {
                    if (stream.StreamIndex < 0 || string.IsNullOrWhiteSpace(stream.Codec))
                        errors.Add(new("source.stream", "Media stream metadata is invalid.", source.Id));
                    if (stream.Kind == MediaStreamKind.Audio && (stream.SampleRate <= 0 || stream.Channels is < 1 or > 2))
                        errors.Add(new("source.audio-stream", "Only valid mono/stereo audio streams are supported.", source.Id));
                }
            }
        }
    }

    private static void ValidateClips(ProjectState project, ICollection<ValidationError> errors)
    {
        foreach (var duplicate in project.MediaClips.GroupBy(item => item.Id).Where(group => group.Count() > 1))
            errors.Add(new("clip.duplicate-id", "Clip IDs must be unique.", duplicate.Key));

        foreach (var clip in project.MediaClips)
        {
            var track = project.FindTrack(clip.TrackId);
            project.Sources.TryGetValue(clip.SourceId, out var source);
            if (clip.Id == Guid.Empty) errors.Add(new("clip.id", "Clip ID cannot be empty."));
            if (track is null) errors.Add(new("clip.track", "Clip references a missing track.", clip.Id));
            if (source is null) errors.Add(new("clip.source", "Clip references a missing source.", clip.Id));
            if (clip.Start < TimelineTime.Zero || clip.SourceIn < TimelineTime.Zero || clip.Duration <= TimelineTime.Zero)
                errors.Add(new("clip.time", "Clip timing is invalid.", clip.Id));
            if (track is not null && source is not null)
            {
                if (track.Kind == TrackKind.Visual && source.Kind is not (MediaKind.Video or MediaKind.Image))
                    errors.Add(new("clip.visual-source", "Visual tracks accept video and images only.", clip.Id));
                if (track.Kind == TrackKind.Audio && !source.HasAudio)
                    errors.Add(new("clip.audio-source", "Audio tracks require a source with audio.", clip.Id));
                if (track.Kind == TrackKind.Text)
                    errors.Add(new("clip.text-track", "Media clips cannot be placed on a text track.", clip.Id));
                if (source.Kind != MediaKind.Image && clip.SourceIn + clip.Duration > source.Duration)
                    errors.Add(new("clip.source-range", "Clip range exceeds source duration.", clip.Id));
            }
            ValidateEffects(clip, errors);
        }

        foreach (var trackGroup in project.MediaClips.GroupBy(item => item.TrackId))
        {
            var ordered = trackGroup.OrderBy(item => item.Start).ThenBy(item => item.Id).ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                if (ordered[index].Start < ordered[index - 1].End)
                    errors.Add(new("clip.overlap", "Clips on the same track cannot overlap.", ordered[index].Id));
            }
        }

        foreach (var linkGroup in project.MediaClips.Where(item => item.LinkGroupId.HasValue).GroupBy(item => item.LinkGroupId!.Value))
        {
            var clips = linkGroup.ToArray();
            if (clips.Length < 2)
            {
                errors.Add(new("link.orphan", "A link group must contain at least two clips.", linkGroup.Key));
                continue;
            }
            var first = clips[0];
            if (clips.Any(item => item.Start != first.Start || item.SourceIn != first.SourceIn || item.Duration != first.Duration))
                errors.Add(new("link.timing", "Linked clips must have identical timeline and source ranges.", linkGroup.Key));
            if (clips.GroupBy(item => project.FindTrack(item.TrackId)?.Kind).Any(group => group.Count() > 1))
                errors.Add(new("link.kind", "A link group may contain only one clip of each track kind.", linkGroup.Key));
        }
    }

    private static void ValidateEffects(MediaClip clip, ICollection<ValidationError> errors)
    {
        if (clip.Video is { } video &&
            (video.Brightness is < -1 or > 1 || video.Contrast is < 0 or > 3 ||
             video.Saturation is < 0 or > 3 || video.Temperature is < -1 or > 1 ||
             video.PositionX is < -5 or > 5 || video.PositionY is < -5 or > 5 ||
             video.ScaleX is <= 0 or > 100 || video.ScaleY is <= 0 or > 100 ||
             video.Rotation is < -360 or > 360 || video.CropLeft is < 0 or > 1 ||
             video.CropTop is < 0 or > 1 || video.CropRight is < 0 or > 1 ||
             video.CropBottom is < 0 or > 1 || video.CropLeft + video.CropRight >= 1 ||
             video.CropTop + video.CropBottom >= 1 || video.Opacity is < 0 or > 1))
            errors.Add(new("clip.video-parameters", "Video parameters are outside the supported range.", clip.Id));
        if (clip.Audio is { } audio &&
            (audio.Volume is < 0 or > 2 || audio.Pan is < -1 or > 1 ||
             audio.Bass is < -20 or > 20 || audio.Mid is < -20 or > 20 || audio.Treble is < -20 or > 20 ||
             audio.FadeIn < TimelineTime.Zero || audio.FadeOut < TimelineTime.Zero ||
             audio.FadeIn > clip.Duration || audio.FadeOut > clip.Duration))
            errors.Add(new("clip.audio-parameters", "Audio parameters are outside the supported range.", clip.Id));
    }

    private static void ValidateText(ProjectState project, ICollection<ValidationError> errors)
    {
        foreach (var duplicate in project.TextClips.GroupBy(item => item.Id).Where(group => group.Count() > 1))
            errors.Add(new("text.duplicate-id", "Text clip IDs must be unique.", duplicate.Key));
        foreach (var clip in project.TextClips)
        {
            var track = project.FindTrack(clip.TrackId);
            if (track?.Kind != TrackKind.Text) errors.Add(new("text.track", "Text clip requires a text track.", clip.Id));
            if (clip.Start < TimelineTime.Zero || clip.Duration <= TimelineTime.Zero)
                errors.Add(new("text.time", "Text clip timing is invalid.", clip.Id));
            if (string.IsNullOrEmpty(clip.Text)) errors.Add(new("text.empty", "Text clip content cannot be empty.", clip.Id));
            if (clip.Style.FontSize is < 4 or > 500 || clip.Style.X is < 0 or > 1 || clip.Style.Y is < 0 or > 1 ||
                clip.Style.BoxWidth is <= 0 or > 1 || clip.Style.BoxHeight is <= 0 or > 1)
                errors.Add(new("text.style", "Text style is outside the supported range.", clip.Id));
        }
        foreach (var group in project.TextClips.GroupBy(item => item.TrackId))
        {
            var ordered = group.OrderBy(item => item.Start).ThenBy(item => item.Id).ToArray();
            for (var index = 1; index < ordered.Length; index++)
                if (ordered[index].Start < ordered[index - 1].End)
                    errors.Add(new("text.overlap", "Text clips on the same track cannot overlap.", ordered[index].Id));
        }
    }

    private static void ValidateMarkers(ProjectState project, ICollection<ValidationError> errors)
    {
        foreach (var duplicate in project.Markers.GroupBy(item => item.Id).Where(group => group.Count() > 1))
            errors.Add(new("marker.duplicate-id", "Marker IDs must be unique.", duplicate.Key));
        foreach (var marker in project.Markers)
        {
            if (marker.Start < TimelineTime.Zero || marker.Duration <= TimelineTime.Zero)
                errors.Add(new("marker.time", "Marker timing is invalid.", marker.Id));
            if (marker.Confidence is < 0 or > 1)
                errors.Add(new("marker.confidence", "Marker confidence must be between 0 and 1.", marker.Id));
        }
    }

    private static void ValidateTransitions(ProjectState project, ICollection<ValidationError> errors)
    {
        foreach (var duplicate in project.Transitions.GroupBy(item => item.Id).Where(group => group.Count() > 1))
            errors.Add(new("transition.duplicate-id", "Transition IDs must be unique.", duplicate.Key));
        foreach (var transition in project.Transitions)
        {
            var track = project.FindTrack(transition.TrackId);
            var from = project.FindMediaClip(transition.FromClipId);
            var to = project.FindMediaClip(transition.ToClipId);
            if (transition.Id == Guid.Empty || track is null || from is null || to is null)
            {
                errors.Add(new("transition.reference", "Transition references a missing entity.", transition.Id));
                continue;
            }
            if (from.TrackId != track.Id || to.TrackId != track.Id || from.End != to.Start)
                errors.Add(new("transition.adjacency", "Transition clips must be adjacent on the same track.", transition.Id));
            if (transition.Duration <= TimelineTime.Zero || transition.Duration > from.Duration || transition.Duration > to.Duration)
                errors.Add(new("transition.duration", "Transition duration exceeds available clip material.", transition.Id));
            if (transition.Start < from.Start || transition.End > to.End || !transition.Range.Contains(from.End))
                errors.Add(new("transition.range", "Transition must straddle the edit point.", transition.Id));
            if (project.Sources.TryGetValue(from.SourceId, out var fromSource) &&
                fromSource.Kind != MediaKind.Image &&
                from.SourceIn + from.Duration + (transition.End - from.End) > fromSource.Duration)
                errors.Add(new("transition.from-handle", "The outgoing source has insufficient media after the edit.", transition.Id));
            if (transition.Start < to.Start && to.SourceIn < to.Start - transition.Start)
                errors.Add(new("transition.to-handle", "The incoming source has insufficient media before the edit.", transition.Id));
            if (track.Kind == TrackKind.Audio && transition.Kind != TransitionKind.ConstantPowerAudio)
                errors.Add(new("transition.audio-kind", "Audio tracks support Constant Power transitions only.", transition.Id));
            if (track.Kind == TrackKind.Visual && transition.Kind == TransitionKind.ConstantPowerAudio)
                errors.Add(new("transition.video-kind", "Visual tracks require a video transition.", transition.Id));
        }
    }

    private static void ValidateInOut(ProjectState project, ICollection<ValidationError> errors)
    {
        if (project.InPoint < TimelineTime.Zero || project.OutPoint < TimelineTime.Zero)
            errors.Add(new("inout.negative", "In/Out points cannot be negative."));
        if (project.InPoint is { } start && project.OutPoint is { } end && end <= start)
            errors.Add(new("inout.order", "Out point must be after In point."));
    }

    private static void ValidateSequenceWorkspace(ProjectState project, ICollection<ValidationError> errors)
    {
        if (project.Sequences.IsDefaultOrEmpty)
        {
            if (project.ActiveSequenceId is not null)
                errors.Add(new("sequence.active-without-items", "Active sequence cannot be set when the project has no sequences."));
            return;
        }

        foreach (var duplicate in project.Sequences.GroupBy(item => item.Id).Where(group => group.Count() > 1))
            errors.Add(new("sequence.duplicate-id", "Sequence IDs must be unique.", duplicate.Key));
        if (project.ActiveSequenceId is not Guid activeId || project.FindSequence(activeId) is null)
        {
            errors.Add(new("sequence.active", "Active sequence is missing."));
            return;
        }

        var active = project.FindSequence(activeId)!;
        if (!active.Matches(project))
            errors.Add(new("sequence.snapshot", "Active sequence snapshot is not synchronized with the live timeline.", active.Id));

        foreach (var sequence in project.Sequences)
        {
            if (sequence.Id == Guid.Empty || string.IsNullOrWhiteSpace(sequence.Name) || sequence.Revision < 0)
            {
                errors.Add(new("sequence.identity", "Sequence identity or revision is invalid.", sequence.Id));
                continue;
            }
            if (sequence.ParentSequenceId is { } parentId && project.FindSequence(parentId) is null)
                errors.Add(new("sequence.parent", "Sequence parent is missing.", sequence.Id));
            if (sequence.MontagePlanId is { } planId && project.FindMontagePlan(planId) is null)
                errors.Add(new("sequence.plan", "Sequence montage plan is missing.", sequence.Id));

            if (sequence.Id == activeId) continue;
            var view = project with
            {
                Sequences = [],
                ActiveSequenceId = null,
                Sequence = sequence.Settings,
                Tracks = sequence.Tracks,
                MediaClips = sequence.MediaClips,
                TextClips = sequence.TextClips,
                Transitions = sequence.Transitions,
                Markers = sequence.Markers,
                InPoint = sequence.InPoint,
                OutPoint = sequence.OutPoint
            };
            ValidateProject(view, errors);
            ValidateTracks(view, errors);
            ValidateClips(view, errors);
            ValidateText(view, errors);
            ValidateTransitions(view, errors);
            ValidateMarkers(view, errors);
            ValidateInOut(view, errors);
        }
    }

    private static void ValidateSourceAnnotations(ProjectState project, ICollection<ValidationError> errors)
    {
        foreach (var duplicate in project.SourceAnnotations.GroupBy(item => item.Id).Where(group => group.Count() > 1))
            errors.Add(new("annotation.duplicate-id", "Source annotation IDs must be unique.", duplicate.Key));
        foreach (var annotation in project.SourceAnnotations)
        {
            if (!project.Sources.TryGetValue(annotation.SourceId, out var source))
            {
                errors.Add(new("annotation.source", "Source annotation references missing media.", annotation.Id));
                continue;
            }
            if (annotation.SourceRange.Start < TimelineTime.Zero || annotation.SourceRange.Duration <= TimelineTime.Zero ||
                annotation.SourceRange.End > source.Duration)
                errors.Add(new("annotation.range", "Source annotation is outside the media range.", annotation.Id));
        }
    }

    private static void ValidateMontagePlans(ProjectState project, ICollection<ValidationError> errors)
    {
        foreach (var duplicate in project.MontagePlans.GroupBy(item => item.Id).Where(group => group.Count() > 1))
            errors.Add(new("montage-plan.duplicate-id", "Montage plan IDs must be unique.", duplicate.Key));
        foreach (var plan in project.MontagePlans)
        {
            if (plan.Id == Guid.Empty || plan.Dependencies.ProjectId != project.Id || string.IsNullOrWhiteSpace(plan.Title))
                errors.Add(new("montage-plan.identity", "Montage plan identity is invalid.", plan.Id));
            if (plan.Items.Select(item => item.Order).Distinct().Count() != plan.Items.Length)
                errors.Add(new("montage-plan.order", "Montage plan item order must be unique.", plan.Id));
            if (plan.Items.Select(item => item.Id).Distinct().Count() != plan.Items.Length)
                errors.Add(new("montage-plan.item-id", "Montage plan item IDs must be unique.", plan.Id));
            foreach (var item in plan.Items)
            {
                if (!project.Sources.TryGetValue(item.SourceId, out var source) ||
                    item.SourceRange.Start < TimelineTime.Zero || item.SourceRange.Duration <= TimelineTime.Zero ||
                    source is not null && item.SourceRange.End > source.Duration)
                    errors.Add(new("montage-plan.range", "Montage plan item references an invalid source range.", item.Id));
                if (item.Confidence is < 0 or > 1 || item.Volume is < 0 or > 2)
                    errors.Add(new("montage-plan.parameters", "Montage plan item parameters are invalid.", item.Id));
            }
        }
    }
}
