using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace Karu;

public record Bookmark(string Title, string Url);

public class BookmarkStore
{
    static readonly string FilePath = Path.Combine(Paths.AppDataDir, "bookmarks.json");
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public ObservableCollection<Bookmark> Items { get; } = new();

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var list = JsonSerializer.Deserialize<List<Bookmark>>(File.ReadAllText(FilePath)) ?? new();
            Items.Clear();
            foreach (var b in list)
            {
                // 手編集で混入した null 要素や Url 欠落は捨てる (残すと Contains 等で落ちる)
                if (b?.Url is not { Length: > 0 }) continue;
                Items.Add(string.IsNullOrEmpty(b.Title) ? b with { Title = b.Url } : b);
            }
        }
        catch { /* 壊れたファイルは無視して空で開始 */ }
    }

    void Save()
    {
        try { Paths.AtomicWrite(FilePath, JsonSerializer.Serialize(Items.ToList(), JsonOpts)); }
        catch { }
    }

    public bool Contains(string url) => Items.Any(b => b.Url == url);

    public void Add(string title, string url)
    {
        var existing = Items.FirstOrDefault(b => b.Url == url);
        if (existing != null) Items.Remove(existing);
        Items.Insert(0, new Bookmark(string.IsNullOrWhiteSpace(title) ? url : title.Trim(), url));
        Save();
    }

    public void RemoveByUrl(string url)
    {
        var existing = Items.FirstOrDefault(b => b.Url == url);
        if (existing != null) { Items.Remove(existing); Save(); }
    }

    public void Remove(Bookmark b)
    {
        Items.Remove(b);
        Save();
    }
}
