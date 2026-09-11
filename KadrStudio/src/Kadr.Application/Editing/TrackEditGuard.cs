using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Editing;

internal static class TrackEditGuard
{
    public static ProjectState Apply(IEditCommand command, ProjectState before)
    {
        var after = command.Apply(before);
        var oldSequences = Timelines(before);
        var newSequences = Timelines(after);
        foreach (var old in oldSequences)
        {
            var locked = old.Tracks.Where(track => track.IsLocked).ToArray();
            if (locked.Length == 0) continue;
            var current = newSequences.FirstOrDefault(sequence => sequence.Id == old.Id);
            if (current is null && before.ActiveSequence is null)
                current = newSequences.FirstOrDefault(sequence => sequence.Matches(before));
            foreach (var track in locked)
            {
                if (current is null || !current.Tracks.Any(item => item.Id == track.Id) ||
                    !old.MediaClips.Where(item => item.TrackId == track.Id).SequenceEqual(current.MediaClips.Where(item => item.TrackId == track.Id)) ||
                    !old.SubtitleClips.Where(item => item.TrackId == track.Id).SequenceEqual(current.SubtitleClips.Where(item => item.TrackId == track.Id)) ||
                    !old.TextClips.Where(item => item.TrackId == track.Id).SequenceEqual(current.TextClips.Where(item => item.TrackId == track.Id)) ||
                    !old.Transitions.Where(item => item.TrackId == track.Id).SequenceEqual(current.Transitions.Where(item => item.TrackId == track.Id)))
                    throw new EditRejectedException($"Дорожка «{track.Name}» заблокирована. Сначала снимите блокировку.");
            }
        }
        return after;
    }

    private static SequenceState[] Timelines(ProjectState project)
        => project.ActiveSequence is null
            ? [SequenceState.Capture(project, Guid.Empty, project.Name)]
            : project.SynchronizeActiveSequence(incrementRevision: false).Sequences.ToArray();
}
