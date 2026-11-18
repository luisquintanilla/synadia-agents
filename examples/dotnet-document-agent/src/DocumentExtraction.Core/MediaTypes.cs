namespace Synadia.Examples.DocumentAgent;

/// <summary>Maps attachment filenames to media types and classifies what each extractor accepts.</summary>
public static class MediaTypes
{
    public const string Pdf = "application/pdf";
    public const string OctetStream = "application/octet-stream";

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = Pdf,
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".markdown"] = "text/markdown",
        [".csv"] = "text/csv",
        [".json"] = "application/json",
    };

    /// <summary>
    /// Resolves the media type for an attachment. The wire envelope only carries
    /// <c>filename</c> + base64 <c>content</c> (no media type), so a host usually
    /// sees <c>application/octet-stream</c>; the filename extension wins in that case.
    /// </summary>
    public static string Resolve(string? declaredMediaType, string? fileName)
    {
        if (!string.IsNullOrWhiteSpace(declaredMediaType) &&
            !string.Equals(declaredMediaType, OctetStream, StringComparison.OrdinalIgnoreCase))
        {
            return declaredMediaType;
        }

        var ext = Path.GetExtension(fileName ?? string.Empty);
        return ext.Length > 0 && ByExtension.TryGetValue(ext, out var mt) ? mt : OctetStream;
    }

    public static bool IsPdf(string mediaType) => string.Equals(mediaType, Pdf, StringComparison.OrdinalIgnoreCase);

    public static bool IsImage(string mediaType) => mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    public static bool IsText(string mediaType) =>
        mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase);
}
