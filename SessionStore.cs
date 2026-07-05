using System.IO;
using System.Text.Json;

namespace Karu;

static class SessionStore
{
    static readonly string FilePath = Path.Combine(Paths.AppDataDir, "session.json");

    public static string[] Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return Array.Empty<string>();
            return JsonSerializer.Deserialize<string[]>(File.ReadAllText(FilePath)) ?? Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    public static void Save(IEnumerable<string> urls)
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(urls.ToArray())); }
        catch { }
    }
}
