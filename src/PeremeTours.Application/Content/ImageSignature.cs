namespace PeremeTours.Application.Content;

// Recognises the image formats the site stores from their first bytes, so a file's declared type cannot lie.
public static class ImageSignature
{
    public static string? Detect(ReadOnlySpan<byte> content)
    {
        if (content.Length >= 3 && content[0] == 0xff && content[1] == 0xd8 && content[2] == 0xff) return "image/jpeg";
        if (content.Length >= 8 && content[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })) return "image/png";
        if (content.Length >= 12 && content[..4].SequenceEqual("RIFF"u8) && content.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }

    public static bool Matches(ReadOnlySpan<byte> content, string contentType) =>
        Detect(content) is { } detected && detected.Equals(contentType, StringComparison.OrdinalIgnoreCase);
}
