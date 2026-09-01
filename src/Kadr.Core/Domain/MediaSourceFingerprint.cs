namespace KadrStudio.Core.Domain;

public static class MediaSourceFingerprint
{
    public static string Stable(MediaSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return !string.IsNullOrWhiteSpace(source.VerifiedFingerprint) ? source.VerifiedFingerprint :
            !string.IsNullOrWhiteSpace(source.FastFingerprint) ? source.FastFingerprint :
            !string.IsNullOrWhiteSpace(source.Fingerprint) ? source.Fingerprint :
            $"{source.FileSize:x}-{source.LastWriteUtcTicks:x}";
    }
}
