using KadrStudio.Application.Editing;
using KadrStudio.Application.Media;

namespace KadrStudio.Services;

public static class MediaRelinkFileValidator
{
    public static void Validate(MediaRelinkPreview preview)
    {
        foreach (var candidate in preview.Candidates)
        {
            var file = new FileInfo(candidate.CandidatePath);
            if (!file.Exists || candidate.Probe is null || file.Length != candidate.Probe.Fingerprint.Length ||
                file.LastWriteTimeUtc.Ticks != candidate.Probe.Fingerprint.LastWriteUtcTicks)
                throw new EditRejectedException("Файл изменился после проверки. Повторите поиск соответствий.");
        }
    }
}
