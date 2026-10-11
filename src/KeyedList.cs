using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Karu;

/// <summary>
/// キー付き一覧オーバーレイ(タブ一覧・お気に入り一覧)の行と選択状態。
/// 行 = [キーバッジ][状態(任意)][タイトル]。j/k やマウスホバーで選択を動かし、Enter/クリックで決定、
/// 行頭のキー1文字で直接選ぶ。両一覧で見た目と操作感を揃えるため、行の組み立てと選択ハイライトをここに集める。
/// 一覧の中身(何を並べるか・決定時に何をするか)は呼び出し側が <see cref="Row"/> で渡す。
/// </summary>
sealed class KeyedList
{
    /// <summary>1行ぶんの表示内容と決定時の動作。State は null なら列ごと出さない。</summary>
    public readonly record struct Row(string Title, Brush TitleBrush, string? State, string? ToolTip, Action Open);

    static readonly Brush BadgeBrush = new SolidColorBrush(Color.FromRgb(0xD4, 0x84, 0x5E));
    static readonly Brush StateBrush = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));
    static readonly Brush SelectedBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));

    readonly Panel _panel;
    readonly string _keys;
    readonly List<Border> _rows = new();
    int _selected;

    /// <param name="panel">行を並べる StackPanel 等。</param>
    /// <param name="keys">行頭に振るキーの並び。i 番目の行は keys[i] で直接選べる(あふれた行はj/kとマウスで)。</param>
    public KeyedList(Panel panel, string keys)
    {
        _panel = panel;
        _keys = keys;
    }

    /// <summary>現在の選択行(0始まり)。</summary>
    public int Selected => _selected;

    /// <summary>行を全部作り直す。selected は行数に収まるよう丸める。</summary>
    public void Build(IReadOnlyList<Row> rows, int selected)
    {
        Clear();
        _selected = Math.Clamp(selected, 0, Math.Max(0, rows.Count - 1));
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            int idx = i;
            var key = i < _keys.Length ? _keys[i].ToString() : "";
            var line = new DockPanel();
            var badge = new Border
            {
                BorderBrush = BadgeBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Width = 22,
                Height = 22,
                Margin = new Thickness(0, 0, 10, 0),
                // 直接ジャンプキーは keys の数まで。あふれた行はj/k選択とマウスで操作する
                Visibility = key.Length > 0 ? Visibility.Visible : Visibility.Hidden,
                Child = new TextBlock
                {
                    Text = key,
                    Foreground = BadgeBrush,
                    FontFamily = new FontFamily("Consolas"),
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            DockPanel.SetDock(badge, Dock.Left);
            line.Children.Add(badge);
            if (row.State is not null)
            {
                var state = new TextBlock
                {
                    Text = row.State,
                    Foreground = StateBrush,
                    Width = 26,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                DockPanel.SetDock(state, Dock.Left);
                line.Children.Add(state);
            }
            line.Children.Add(new TextBlock
            {
                Text = row.Title,
                ToolTip = row.ToolTip,
                Foreground = row.TitleBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var wrap = new Border
            {
                Padding = new Thickness(8, 5, 8, 5),
                CornerRadius = new CornerRadius(6),
                Background = i == _selected ? SelectedBrush : Brushes.Transparent,
                Child = line,
                Cursor = Cursors.Hand,
            };
            wrap.MouseEnter += (_, _) => Select(idx);
            wrap.MouseLeftButtonUp += (_, _) => row.Open();
            _rows.Add(wrap);
            _panel.Children.Add(wrap);
        }
    }

    public void Clear()
    {
        _panel.Children.Clear();
        _rows.Clear();
    }

    /// <summary>選択行を移す(端は反対側へループ)。</summary>
    public void Select(int i)
    {
        if (_rows.Count == 0) return;
        _selected = (i + _rows.Count) % _rows.Count;
        for (int r = 0; r < _rows.Count; r++)
            _rows[r].Background = r == _selected ? SelectedBrush : Brushes.Transparent;
        _rows[_selected].BringIntoView();
    }

    public void BringSelectedIntoView()
    {
        if (_rows.Count > 0) _rows[_selected].BringIntoView();
    }

    /// <summary>押されたキーが指す行番号。keys に無いキーは -1。</summary>
    public int IndexOfKey(Key key) => KeyToChar(key) is { } c ? _keys.IndexOf(c) : -1;

    /// <summary>数字(テンキー含む)と英字を行頭キーの1文字に写す。それ以外は null。</summary>
    public static char? KeyToChar(Key key) =>
        key >= Key.D0 && key <= Key.D9 ? (char)('0' + (key - Key.D0)) :
        key >= Key.NumPad0 && key <= Key.NumPad9 ? (char)('0' + (key - Key.NumPad0)) :
        key >= Key.A && key <= Key.Z ? (char)('a' + (key - Key.A)) : null;
}
