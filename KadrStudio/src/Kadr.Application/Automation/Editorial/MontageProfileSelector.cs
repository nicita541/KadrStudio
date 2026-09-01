using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Automation.Editorial;

public static class MontageProfileSelector
{
    public static MontageProfileKind Suggest(string request)
    {
        var text = (request ?? string.Empty).ToLowerInvariant();
        if (ContainsAny(text, "аниме", "anime", "опенинг", "opening", "эндинг", "ending"))
            return MontageProfileKind.AnimeEpisode;
        if (ContainsAny(text, "подкаст", "podcast", "интервью без видео"))
            return MontageProfileKind.Podcast;
        if (ContainsAny(text, "shorts", "reels", "tiktok", "шортс", "вертикаль", "хайлайт"))
            return MontageProfileKind.ShortFormHighlights;
        if (ContainsAny(text, "говорящ", "talking head", "лекци", "вебинар", "интервью"))
            return MontageProfileKind.TalkingHead;
        if (ContainsAny(text, "фильм", "сериал", "эпизод", "серия", "кино"))
            return MontageProfileKind.FilmSeries;
        return MontageProfileKind.Generic;
    }

    private static bool ContainsAny(string source, params string[] candidates)
        => candidates.Any(candidate => source.Contains(candidate, StringComparison.Ordinal));
}
