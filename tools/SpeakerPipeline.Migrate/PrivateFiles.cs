using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpeakerPipeline.Migrate;

internal static class PrivateFiles
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public static string ValidatePath(string path)
    {
        var full = Path.GetFullPath(path);
        var cursor = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full).Directory;
        for (; cursor is not null; cursor = cursor.Parent)
        {
            if (File.Exists(Path.Combine(cursor.FullName, ".git")) || Directory.Exists(Path.Combine(cursor.FullName, ".git")))
                throw new ArgumentException("Private migration files must be outside every Git working tree.");
            if (cursor.LinkTarget is not null)
            {
                var resolved = cursor.ResolveLinkTarget(returnFinalTarget: true)
                    ?? throw new ArgumentException("Cannot resolve migration directory link.");
                ValidatePath(resolved.FullName);
            }
        }
        if (File.Exists(full) && new FileInfo(full).LinkTarget is not null)
            ValidatePath(new FileInfo(full).ResolveLinkTarget(true)!.FullName);
        return full;
    }

    public static T Read<T>(string path)
        => JsonSerializer.Deserialize<T>(File.ReadAllText(ValidatePath(path)), Json)
            ?? throw new ArgumentException("Input JSON cannot be null.");

    public static void WriteNew<T>(string path, T value)
    {
        var full = ValidatePath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var file = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(file, value, Json);
        file.Flush(flushToDisk: true);
    }
}
