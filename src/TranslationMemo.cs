namespace Karu;

/// <summary>
/// 翻訳結果のプロセス共有LRUメモ。件数だけでなく文字数も制限し、
/// 長文ページを渡り歩いたときに入力と訳文が無制限に常駐するのを防ぐ。
/// </summary>
internal sealed class TranslationMemo
{
    readonly int _entryCap;
    readonly int _characterCap;
    readonly object _lock = new();
    readonly Dictionary<string, LinkedListNode<Entry>> _entries = new();
    readonly LinkedList<Entry> _order = new();
    long _characters;

    readonly record struct Entry(string Key, string Translation, string? Detected)
    {
        public long Characters => (long)Key.Length + Translation.Length + (Detected?.Length ?? 0);
    }

    public TranslationMemo(int entryCap, int characterCap)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entryCap, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(characterCap, 1);
        _entryCap = entryCap;
        _characterCap = characterCap;
    }

    public (string Translation, string? Detected)? Get(string key)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var node)) return null;
            _order.Remove(node);
            _order.AddLast(node);
            return (node.Value.Translation, node.Value.Detected);
        }
    }

    public void Set(string key, string translation, string? detected)
    {
        var entry = new Entry(key, translation, detected);
        lock (_lock)
        {
            if (_entries.Remove(key, out var old))
            {
                _order.Remove(old);
                _characters -= old.Value.Characters;
            }

            // 単体で予算を超える長文は、他のキャッシュを全部追い出さず保持しない。
            if (entry.Characters > _characterCap) return;

            var node = _order.AddLast(entry);
            _entries[key] = node;
            _characters += entry.Characters;
            while (_entries.Count > _entryCap || _characters > _characterCap)
            {
                var oldest = _order.First!;
                _order.RemoveFirst();
                _entries.Remove(oldest.Value.Key);
                _characters -= oldest.Value.Characters;
            }
        }
    }

    internal int Count { get { lock (_lock) return _entries.Count; } }
    internal long Characters { get { lock (_lock) return _characters; } }
}
