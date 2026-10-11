using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Karu;

/// <summary>キーボード操作のオーバーレイ群: URL入力(o / Ctrl+L)・お気に入り名編集(Ctrl+D)・
/// タブ一覧(Ctrl+Tab)・お気に入り一覧(b)。WebView2(ネイティブHWND)はキー入力を奪い、半透明で上に
/// 重ねることもできない(airspace)ため、表示中はアクティブWebViewを隠し、直前のページのぼかし画像を敷く。</summary>
public partial class MainWindow
{
    // ---- オーバーレイ共通 ----
    // WebView2(ネイティブHWND)がキー入力を奪うため、オーバーレイ表示中はアクティブWebViewを隠す

    bool IsOverlayOpen =>
        StarOverlay.Visibility == Visibility.Visible ||
        UrlOverlay.Visibility == Visibility.Visible ||
        TabOverlay.Visibility == Visibility.Visible ||
        BookmarkListOverlay.Visibility == Visibility.Visible;

    void HideActiveViewForOverlay()
    {
        if (_active?.View is not null) _active.View.Visibility = Visibility.Hidden;
    }

    void RestoreActiveViewAfterOverlay()
    {
        if (IsOverlayOpen) return;
        OverlayBackdrop.Visibility = Visibility.Collapsed;
        OverlayBackdrop.Source = null;
        if (_active?.View is not null)
        {
            _active.View.Visibility = Visibility.Visible;
            _active.View.Focus();
        }
    }

    /// <summary>オーバーレイを開く直前に呼ぶ。WebViewを隠す前のページを1枚キャプチャしてぼかして
    /// 敷いてから隠すことで、隠している間も裏でページが見えているかのような背景にする
    /// (真っ暗な単色より「処理中」感が出て、他のページ内オーバーレイ(bキー等)のぼかしとも見た目が揃う)。</summary>
    async Task ShowOverlayBackdrop()
    {
        var core = _active?.View?.CoreWebView2;
        if (core is not null)
        {
            try
            {
                using var stream = new MemoryStream();
                // PNGはフルサイズのエンコードが重くオーバーレイが開くまで待たされる。
                // ぼかして敷くだけなのでJPEG+半分解像度デコードで十分(体感差が出るほど速い)
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, stream);
                stream.Position = 0;
                OverlayBackdrop.Source = DecodeBitmap(stream, Math.Max(320, (int)(WebHost.ActualWidth / 2)));
                OverlayBackdrop.Visibility = Visibility.Visible;
            }
            catch { OverlayBackdrop.Visibility = Visibility.Collapsed; }
        }
        HideActiveViewForOverlay();
    }

    void FocusOverlayBox(TextBox box)
        => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            box.Focus();
            Keyboard.Focus(box);
            box.SelectAll();
        });

    /// <summary>一覧オーバーレイにキーボードフォーカスを移し、選択行を見える位置へ出す。</summary>
    void FocusOverlayList(UIElement host, KeyedList list)
        => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            host.Focus();
            Keyboard.Focus(host);
            list.BringSelectedIntoView();
        });

    // ---- URL / 検索入力オーバーレイ (o / Ctrl+L) ----

    async void ShowUrlOverlay()
    {
        UrlInputBox.Text = ActiveUrl();
        await ShowOverlayBackdrop();
        UrlOverlay.Visibility = Visibility.Visible;
        FocusOverlayBox(UrlInputBox);
    }

    void CloseUrlOverlay()
    {
        UrlOverlay.Visibility = Visibility.Collapsed;
        RestoreActiveViewAfterOverlay();
    }

    void UrlOverlay_MouseDown(object sender, MouseButtonEventArgs e) => CloseUrlOverlay();

    void UrlInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseUrlOverlay(); e.Handled = true; return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var text = UrlInputBox.Text.Trim();
        bool newTab = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        CloseUrlOverlay();
        if (text.Length == 0) return;
        if (newTab) _ = AddTabAsync(text);
        else if (_active is not null) Navigate(_active, text);
    }

    // ---- タブ一覧オーバーレイ (Ctrl+Tab) ----

    // j/k は選択移動に使うため直接ジャンプのキーからは外してある
    const string TabKeys = "1234567890abcdefghilmnopqrstuvwxyz";
    static readonly Brush ActiveTitleBrush = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE));
    static readonly Brush IdleTitleBrush = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
    KeyedList? _tabList;
    KeyedList TabList => _tabList ??= new KeyedList(TabListPanel, TabKeys);

    async void ShowTabOverlay()
    {
        BuildTabRows(_active is null ? 0 : Math.Max(0, Tabs.IndexOf(_active)));
        await ShowOverlayBackdrop();
        TabOverlay.Visibility = Visibility.Visible;
        FocusOverlayList(TabOverlay, TabList);
    }

    void CloseTabOverlay()
    {
        TabOverlay.Visibility = Visibility.Collapsed;
        TabList.Clear();
        RestoreActiveViewAfterOverlay();
    }

    void TabOverlay_MouseDown(object sender, MouseButtonEventArgs e) => CloseTabOverlay();

    void TabListMenu_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        ShowTabOverlay();
    }

    /// <summary>タブ一覧の行を作り直す(一覧を開いたままタブを閉じた後の再描画にも使う)。</summary>
    void BuildTabRows(int selected)
    {
        var rows = new List<KeyedList.Row>(Tabs.Count);
        foreach (var t in Tabs)
        {
            var state = StateOf(t) switch
            {
                TabState.Hibernated => "💤",
                TabState.Suspended => "⏸",
                TabState.Active => "▶",
                _ => "・",
            };
            rows.Add(new KeyedList.Row(t.Title, t == _active ? ActiveTitleBrush : IdleTitleBrush, state, null,
                () => { CloseTabOverlay(); if (Tabs.Contains(t)) ActivateTab(t); }));
        }
        TabList.Build(rows, selected);
    }

    /// <summary>タブ一覧表示中のキー処理。
    /// j/k(↑↓)=選択移動 / Enter=選択タブを開く / Ctrl+W=選択タブを閉じる /
    /// キー=直接切替 / Shift+キー=そのタブを閉じる / Esc・Tab=一覧を閉じる。</summary>
    void HandleTabOverlayKey(Key key, bool shift, bool ctrl)
    {
        if (key == Key.Escape || key == Key.Tab) { CloseTabOverlay(); return; }
        if (ctrl && key == Key.W) { CloseTabInOverlay(TabList.Selected); return; }
        if (ctrl) return; // Ctrl併用キーを直接切替と誤認しない
        if (key == Key.J || key == Key.Down) { TabList.Select(TabList.Selected + 1); return; }
        if (key == Key.K || key == Key.Up) { TabList.Select(TabList.Selected - 1); return; }
        if (key == Key.Enter)
        {
            if (TabList.Selected < Tabs.Count) OpenTabFromOverlay(Tabs[TabList.Selected]);
            return;
        }
        int idx = TabList.IndexOfKey(key);
        if (idx < 0 || idx >= Tabs.Count) return;
        if (shift) CloseTabInOverlay(idx);
        else OpenTabFromOverlay(Tabs[idx]);
    }

    void OpenTabFromOverlay(BrowserTab tab)
    {
        CloseTabOverlay();
        ActivateTab(tab);
    }

    /// <summary>一覧を開いたまま index 番目のタブを閉じ、行を作り直す。
    /// CloseTab 内の ActivateTab で表示された新アクティブはオーバーレイの裏なので隠し直す。</summary>
    void CloseTabInOverlay(int index)
    {
        if (index < Tabs.Count) CloseTab(Tabs[index]);
        BuildTabRows(TabList.Selected);
        HideActiveViewForOverlay();
    }

    // ---- お気に入り名編集オーバーレイ (Ctrl+D) ----

    async void Star_Click(object sender, RoutedEventArgs e)
    {
        var url = ActiveUrl();
        if (url.Length == 0 || _active is null) return;
        if (_bookmarks.Contains(url))
        {
            _bookmarks.RemoveByUrl(url);
        }
        else
        {
            StarNameBox.Text = _active.Title;
            await ShowOverlayBackdrop();
            StarOverlay.Visibility = Visibility.Visible;
            FocusOverlayBox(StarNameBox);
        }
    }

    void CloseStarOverlay()
    {
        StarOverlay.Visibility = Visibility.Collapsed;
        RestoreActiveViewAfterOverlay();
    }

    void StarSave_Click(object sender, RoutedEventArgs e)
    {
        var url = ActiveUrl();
        if (url.Length == 0) return;
        var name = StarNameBox.Text.Trim();
        if (name.Length == 0) name = url;
        _bookmarks.Add(name, url);
        CloseStarOverlay();
    }

    void StarCancel_Click(object sender, RoutedEventArgs e) => CloseStarOverlay();

    void StarOverlay_MouseDown(object sender, MouseButtonEventArgs e) => CloseStarOverlay();
    void Overlay_Inner_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    void StarNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { StarSave_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Escape) { CloseStarOverlay(); e.Handled = true; }
    }

    // ---- お気に入り一覧 (b) ----

    // 選択移動(j/k)と閉じる(b)に使うキーは直接ジャンプから外してある
    const string BookmarkKeys = "1234567890acdefghilmnopqrstuvwyz";
    static readonly Brush BookmarkTitleBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
    KeyedList? _bookmarkList;
    KeyedList BookmarkList => _bookmarkList ??= new KeyedList(BookmarkListPanel, BookmarkKeys);

    /// <summary>ページへURL一覧を渡さず、WPF側だけでお気に入り一覧を表示する。</summary>
    async void ShowBookmarkOverlay()
    {
        if (Bookmarks.Count == 0) return;
        BookmarkList.Build(Bookmarks.Select(b => new KeyedList.Row(
            b.Title, BookmarkTitleBrush, null, b.Url, () => OpenBookmark(b, newTab: false))).ToList(), 0);
        await ShowOverlayBackdrop();
        BookmarkListOverlay.Visibility = Visibility.Visible;
        FocusOverlayList(BookmarkListOverlay, BookmarkList);
    }

    void CloseBookmarkOverlay()
    {
        BookmarkListOverlay.Visibility = Visibility.Collapsed;
        BookmarkList.Clear();
        RestoreActiveViewAfterOverlay();
    }

    void BookmarkListOverlay_MouseDown(object sender, MouseButtonEventArgs e) => CloseBookmarkOverlay();

    void OpenBookmark(Bookmark bookmark, bool newTab)
    {
        CloseBookmarkOverlay();
        if (newTab) _ = AddTabAsync(bookmark.Url);
        else if (_active is not null) Navigate(_active, bookmark.Url);
    }

    /// <summary>お気に入り一覧表示中のキー処理。
    /// j/k(↑↓)=選択移動 / Enter=開く(Shiftで新しいタブ) / キー=直接開く / b・Esc=閉じる。</summary>
    void HandleBookmarkOverlayKey(Key key, bool shift, bool ctrl)
    {
        if (key == Key.Escape || (!ctrl && key == Key.B)) { CloseBookmarkOverlay(); return; }
        if (ctrl) return;
        if (key == Key.J || key == Key.Down) { BookmarkList.Select(BookmarkList.Selected + 1); return; }
        if (key == Key.K || key == Key.Up) { BookmarkList.Select(BookmarkList.Selected - 1); return; }
        if (key == Key.Enter)
        {
            if (BookmarkList.Selected < Bookmarks.Count) OpenBookmark(Bookmarks[BookmarkList.Selected], shift);
            return;
        }
        int idx = BookmarkList.IndexOfKey(key);
        if (idx >= 0 && idx < Bookmarks.Count) OpenBookmark(Bookmarks[idx], shift);
    }
}
