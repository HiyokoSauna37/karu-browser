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
            foreach (var b in list) Items.Add(b);
        }
        catch { /* 壊れたファイルは無視して空で開始 */ }
    }

    void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Items.ToList(), JsonOpts)); }
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
