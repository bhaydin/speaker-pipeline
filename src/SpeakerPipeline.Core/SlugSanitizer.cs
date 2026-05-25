namespace SpeakerPipeline.Core;

/// <summary>
/// Sanitizes strings into Azure Table Storage-safe keys.
/// Forbidden characters in Table Storage keys: '/', '\\', '#', '?', control chars.
/// See docs/architecture-table-storage.md §6 Gotchas.
/// </summary>
public static class SlugSanitizer
{
    private static readonly char[] ForbiddenChars = ['/', '\\', '#', '?'];

    public static bool IsValid(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        foreach (var c in key)
        {
            if (char.IsControl(c) || Array.IndexOf(ForbiddenChars, c) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    public static string Sanitize(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var lowered = raw.Trim().ToLowerInvariant();
        var buffer = new char[lowered.Length];
        var write = 0;
        var lastWasDash = false;

        foreach (var c in lowered)
        {
            if (char.IsLetterOrDigit(c))
            {
                buffer[write++] = c;
                lastWasDash = false;
                continue;
            }

            if (!lastWasDash && write > 0)
            {
                buffer[write++] = '-';
                lastWasDash = true;
            }
        }

        var result = new string(buffer, 0, write).Trim('-');
        return result.Length == 0
            ? throw new ArgumentException($"Cannot derive a slug from '{raw}'.", nameof(raw))
            : result;
    }
}
