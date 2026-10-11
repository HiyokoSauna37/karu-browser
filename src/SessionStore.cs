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
        try { Paths.AtomicWrite(FilePath, JsonSerializer.Serialize(urls.ToArray())); }
        catch { }
    }

    /// <summary>保存済みセッションへ URL を合流させる(順序維持・重複除外)。分離ウィンドウの終了時に使う:
    /// 各窓が自分のタブで丸ごと上書きすると、最後に閉じた窓が他の窓のタブを消してしまう。</summary>
    public static void Append(IEnumerable<string> urls)
    {
        var merged = new List<string>(Load());
        foreach (var u in urls)
            if (!merged.Contains(u)) merged.Add(u);
        Save(merged);
    }
}
