using System.Collections.Immutable;

namespace KadrStudio.Core.Domain;

public static class MontageProfileCatalog
{
    public static ImmutableArray<MontageProfile> All { get; } =
    [
        new(
            "generic", 1, MontageProfileKind.Generic, "Универсальный",
            [CoverageChannel.Frames, CoverageChannel.Motion, CoverageChannel.Audio],
            [EditorialPassKind.StoryContinuity, EditorialPassKind.Rhythm,
             EditorialPassKind.DialogueAudio, EditorialPassKind.CompositionReframe,
             EditorialPassKind.Captions, EditorialPassKind.QualityControl],
            4, ["protected"]),
        new(
            "film-series", 1, MontageProfileKind.FilmSeries, "Фильм / сериал",
            [CoverageChannel.Frames, CoverageChannel.Motion, CoverageChannel.Audio,
             CoverageChannel.Transcript],
            [EditorialPassKind.StoryContinuity, EditorialPassKind.DialogueAudio,
             EditorialPassKind.Rhythm, EditorialPassKind.CompositionReframe,
             EditorialPassKind.Captions, EditorialPassKind.QualityControl],
            5.5, ["dialogue", "plot", "credits", "protected"]),
        new(
            "anime-episode", 1, MontageProfileKind.AnimeEpisode, "Аниме-серия",
            [CoverageChannel.Frames, CoverageChannel.Motion, CoverageChannel.Audio],
            [EditorialPassKind.StoryContinuity, EditorialPassKind.QualityControl],
            5.5,
            ["episode-body", "post-credits", "preview", "recap", "protected"],
            EditScopePolicy.AnimeOpeningEndingOnly),
        new(
            "talking-head", 1, MontageProfileKind.TalkingHead, "Разговор в кадре",
            [CoverageChannel.Frames, CoverageChannel.Audio, CoverageChannel.Transcript],
            [EditorialPassKind.StoryContinuity, EditorialPassKind.DialogueAudio,
             EditorialPassKind.Rhythm, EditorialPassKind.CompositionReframe,
             EditorialPassKind.Captions, EditorialPassKind.QualityControl],
            3.5, ["meaning", "speaker-turn", "name", "protected"]),
        new(
            "podcast", 1, MontageProfileKind.Podcast, "Подкаст",
            [CoverageChannel.Audio, CoverageChannel.Transcript],
            [EditorialPassKind.StoryContinuity, EditorialPassKind.DialogueAudio,
             EditorialPassKind.Rhythm, EditorialPassKind.Captions,
             EditorialPassKind.QualityControl],
            8, ["meaning", "speaker-turn", "protected"]),
        new(
            "short-form-highlights", 1, MontageProfileKind.ShortFormHighlights, "Короткие highlights",
            [CoverageChannel.Frames, CoverageChannel.Motion, CoverageChannel.Audio,
             CoverageChannel.Transcript],
            [EditorialPassKind.StoryContinuity, EditorialPassKind.Rhythm,
             EditorialPassKind.DialogueAudio, EditorialPassKind.CompositionReframe,
             EditorialPassKind.Captions, EditorialPassKind.QualityControl],
            1.8, ["hook", "payoff", "brand", "protected"])
    ];

    public static MontageProfile Get(MontageProfileKind kind)
        => All.Single(profile => profile.Kind == kind);

    public static MontageProfile Get(string id)
        => All.Single(profile => profile.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
