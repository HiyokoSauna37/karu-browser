using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace Karu;

/// <summary>お気に入り・動画集中モード・mpv/Edge連携・メニューなどの機能面。</summary>
public partial class MainWindow
{
    // ---- オーバーレイ共通 ----
    // WebView2(ネイティブHWND)がキー入力を奪うため、オーバーレイ表示中はアクティブWebViewを隠す

    bool IsOverlayOpen =>
        StarOverlay.Visibility == Visibility.Visible ||
        UrlOverlay.Visibility == Visibility.Visible ||
        TabOverlay.Visibility == Visibility.Visible;

    void HideActiveViewForOverlay()
    {
        if (_active?.View is not null) _active.View.Visibility = Visibility.Hidden;
    }

    void RestoreActiveViewAfterOverlay()
    {
        if (IsOverlayOpen) return;
        if (LoadingCover.Visibility == Visibility.Visible) return; // 読み込み被い表示中はそちらの解除に任せる
        if (_active?.View is not null)
        {
            _active.View.Visibility = Visibility.Visible;
            _active.View.Focus();
        }
    }

    void FocusOverlayBox(System.Windows.Controls.TextBox box)
        => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            box.Focus();
            Keyboard.Focus(box);
            box.SelectAll();
        });

    // ---- URL / 検索入力オーバーレイ (o / Ctrl+L) ----

    void ShowUrlOverlay()
    {
        UrlInputBox.Text = ActiveUrl();
        HideActiveViewForOverlay();
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

    static readonly Brush RowSelBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));

    readonly List<System.Windows.Controls.Border> _tabRows = new();
    int _tabSel;

    void ShowTabOverlay()
    {
        _tabSel = _active is null ? 0 : Math.Max(0, Tabs.IndexOf(_active));
        BuildTabRows();
        HideActiveViewForOverlay();
        TabOverlay.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            TabOverlay.Focus();
            Keyboard.Focus(TabOverlay);
            if (_tabRows.Count > 0) _tabRows[_tabSel].BringIntoView();
        });
    }

    void CloseTabOverlay()
    {
        TabOverlay.Visibility = Visibility.Collapsed;
        RestoreActiveViewAfterOverlay();
    }

    void TabOverlay_MouseDown(object sender, MouseButtonEventArgs e) => CloseTabOverlay();

    void TabListMenu_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        ShowTabOverlay();
    }

    void BuildTabRows()
    {
        TabListPanel.Children.Clear();
        _tabRows.Clear();
        _tabSel = Math.Clamp(_tabSel, 0, Math.Max(0, Tabs.Count - 1));
        for (int i = 0; i < Tabs.Count; i++)
        {
            var t = Tabs[i];
            int idx = i;
            var state = t.View is null ? "💤"
                : t.View.CoreWebView2?.IsSuspended == true ? "⏸"
                : t == _active ? "▶" : "・";

            var row = new System.Windows.Controls.DockPanel();
            var badge = new System.Windows.Controls.Border
            {
                BorderBrush = StarOnBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Width = 22,
                Height = 22,
                Margin = new Thickness(0, 0, 10, 0),
                // 直接ジャンプキーはTabKeysの数まで。あふれた行はj/k選択とマウスで操作する
                Visibility = i < TabKeys.Length ? Visibility.Visible : Visibility.Hidden,
                Child = new System.Windows.Controls.TextBlock
                {
                    Text = i < TabKeys.Length ? TabKeys[i].ToString() : "",
                    Foreground = StarOnBrush,
                    FontFamily = new FontFamily("Consolas"),
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            System.Windows.Controls.DockPanel.SetDock(badge, System.Windows.Controls.Dock.Left);
            var stateText = new System.Windows.Controls.TextBlock
            {
                Text = state,
                Foreground = StarOffBrush,
                Width = 26,
                VerticalAlignment = VerticalAlignment.Center,
            };
            System.Windows.Controls.DockPanel.SetDock(stateText, System.Windows.Controls.Dock.Left);
            var title = new System.Windows.Controls.TextBlock
            {
                Text = t.Title,
                Foreground = new SolidColorBrush(t == _active
                    ? Color.FromRgb(0xEE, 0xEE, 0xEE) : Color.FromRgb(0xAA, 0xAA, 0xAA)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(badge);
            row.Children.Add(stateText);
            row.Children.Add(title);
            var wrap = new System.Windows.Controls.Border
            {
                Padding = new Thickness(8, 5, 8, 5),
                CornerRadius = new CornerRadius(6),
                Background = i == _tabSel ? RowSelBrush : Brushes.Transparent,
                Child = row,
                Cursor = Cursors.Hand,
            };
            wrap.MouseEnter += (_, _) => SetTabSel(idx);
            wrap.MouseLeftButtonUp += (_, _) =>
            {
                CloseTabOverlay();
                if (Tabs.Contains(t)) ActivateTab(t);
            };
            _tabRows.Add(wrap);
            TabListPanel.Children.Add(wrap);
        }
    }

    /// <summary>タブ一覧の選択カーソルを移動する (端は反対側へループ)。</summary>
    void SetTabSel(int i)
    {
        if (_tabRows.Count == 0) return;
        _tabSel = (i + _tabRows.Count) % _tabRows.Count;
        for (int r = 0; r < _tabRows.Count; r++)
            _tabRows[r].Background = r == _tabSel ? RowSelBrush : Brushes.Transparent;
        _tabRows[_tabSel].BringIntoView();
    }

    /// <summary>タブ一覧表示中のキー処理。
    /// j/k(↑↓)=選択移動 / Enter=選択タブを開く / Ctrl+W=選択タブを閉じる /
    /// キー=直接切替 / Shift+キー=そのタブを閉じる / Esc・Tab=一覧を閉じる。</summary>
    void HandleTabOverlayKey(Key key, bool shift, bool ctrl)
    {
        if (key == Key.Escape || key == Key.Tab) { CloseTabOverlay(); return; }
        if (ctrl && key == Key.W)
        {
            if (_tabSel < Tabs.Count) CloseTab(Tabs[_tabSel]);
            BuildTabRows();
            HideActiveViewForOverlay(); // CloseTab内のActivateTabで表示された新アクティブを隠し直す
            return;
        }
        if (ctrl) return; // Ctrl併用キーを直接切替と誤認しない
        if (key == Key.J || key == Key.Down) { SetTabSel(_tabSel + 1); return; }
        if (key == Key.K || key == Key.Up) { SetTabSel(_tabSel - 1); return; }
        if (key == Key.Enter)
        {
            if (_tabSel >= Tabs.Count) return;
            var sel = Tabs[_tabSel];
            CloseTabOverlay();
            ActivateTab(sel);
            return;
        }
        char? c =
            key >= Key.D0 && key <= Key.D9 ? (char)('0' + (key - Key.D0)) :
            key >= Key.NumPad0 && key <= Key.NumPad9 ? (char)('0' + (key - Key.NumPad0)) :
            key >= Key.A && key <= Key.Z ? (char)('a' + (key - Key.A)) : null;
        if (c is null) return;
        int idx = TabKeys.IndexOf(c.Value);
        if (idx < 0 || idx >= Tabs.Count) return;
        if (shift)
        {
            CloseTab(Tabs[idx]);
            BuildTabRows();
            HideActiveViewForOverlay(); // CloseTab内のActivateTabで表示された新アクティブを隠し直す
        }
        else
        {
            var t = Tabs[idx];
            CloseTabOverlay();
            ActivateTab(t);
        }
    }

    // ---- お気に入り (Ctrl+D 名前編集オーバーレイ / b キー一覧) ----

    void Star_Click(object sender, RoutedEventArgs e)
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
            HideActiveViewForOverlay();
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

    /// <summary>Vim層の b キー: ページ内にお気に入り一覧オーバーレイを出す</summary>
    async void ShowBookmarkOverlay(BrowserTab tab)
    {
        var core = tab.View?.CoreWebView2;
        if (core is null) return;
        var list = Bookmarks.Select(b => new { title = b.Title, url = b.Url }).ToList();
        var json = JsonSerializer.Serialize(list);
        var script = Injections.BookmarkOverlay.Replace("{0}", json);
        try { await core.ExecuteScriptAsync(script); } catch { }
    }

    void BookmarkOpen_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        if (((FrameworkElement)sender).Tag is Bookmark b && _active is not null)
            Navigate(_active, b.Url);
    }

    void BookmarkDelete_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is Bookmark b) _bookmarks.Remove(b);
        NoBookmarksText.Visibility = Bookmarks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- 動画集中モード ----

    async void Focus_Click(object sender, RoutedEventArgs e)
    {
        _settings.FocusMode = !_settings.FocusMode;
        SettingsStore.Save(_settings);
        RefreshFocusUi();
        foreach (var t in Tabs)
        {
            var c = t.View?.CoreWebView2;
            if (c is null) continue;
            try { await c.ExecuteScriptAsync(_settings.FocusMode ? Injections.ToggleFocusOn : Injections.ToggleFocusOff); }
            catch { }
        }
    }

    void RefreshFocusUi()
        => FocusMenuBtn.Content = _settings.FocusMode
            ? "動画集中モード: ON — コメント・関連動画を非表示（クリックでOFF）"
            : "動画集中モード: OFF（クリックでON）";

    // ---- メニュー ----

    void Menu_Click(object sender, RoutedEventArgs e)
    {
        NoBookmarksText.Visibility = Bookmarks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MenuPopup.IsOpen = true;
    }

    void OpenConfig_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        try { Process.Start(new ProcessStartInfo("explorer.exe", Paths.AppDataDir) { UseShellExecute = true }); }
        catch { }
    }

    // ---- 外部プレーヤー連携 (mpv / Edge) ----

    void Mpv_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        OpenInMpv();
    }

    void OpenEdge_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        OpenInEdge();
    }

    /// <summary>現在のページの動画を mpv (+yt-dlp) で開く。
    /// mpvが5秒生存したら（=再生成功とみなして）元ページを数KBのプレースホルダーに差し替え、
    /// 視聴中のChromium消費をほぼゼロにする。DRMサイトは最初から案内だけ出す。</summary>
    async void OpenInMpv()
    {
        var tab = _active;
        var url = ActiveUrl();
        var core = tab?.View?.CoreWebView2;
        if (url.Length == 0 || core is null || tab is null) return;
        if (Mpv.IsDrmSite(url))
        {
            MessageBox.Show(this,
                "このサイトはDRM保護のため mpv では再生できません。\nCtrl+E で Edge で開いてください。", "Karu");
            return;
        }
        double pos = 0;
        bool isLive = false, hasVideo = false;
        try
        {
            var r = await core.ExecuteScriptAsync("""
                (function(){
                  var v=document.querySelector('video');
                  if(!v) return '0,0,0';
                  var live = !isFinite(v.duration)
                    || !!document.querySelector('.ytp-live-badge[disabled]')
                    || !!document.querySelector('.ytp-live');
                  var t = v.currentTime || 0;
                  v.pause();
                  return t + ',' + (live?'1':'0') + ',1';
                })()
                """);
            var parts = r.Trim('"').Split(',');
            double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out pos);
            if (parts.Length > 1) isLive = parts[1] == "1";
            if (parts.Length > 2) hasVideo = parts[2] == "1";
        }
        catch { }
        var proc = Mpv.TryLaunch(url, pos, isLive);
        if (proc is null)
        {
            MessageBox.Show(this,
                "mpv が見つかりませんでした。\nwinget install mpv-player.mpv-CI.MSVC でインストールできます。", "Karu");
            return;
        }
        if (!hasVideo) return; // 動画のないページはそのまま残す

        // 5秒後もmpvが生きていたら再生成功とみなしてページを畳む。
        // 失敗(即終了)ならページは一時停止のまま残るので何も壊れない。
        await Task.Delay(5000);
        bool alive;
        try { alive = !proc.HasExited; } catch { alive = true; }
        if (!alive) return;
        if (!Tabs.Contains(tab)) return;                       // その間にタブが閉じられた
        var c = tab.View?.CoreWebView2;
        if (c is null) return;
        try { if (c.Source != url) return; } catch { return; } // 別ページへ移動済みなら触らない
        tab.MpvReturnUrl = url;
        tab.MpvReturnPos = isLive ? 0 : pos;
        tab.SuppressCoverOnce = true; // 内部プレースホルダーへの差し替えに読み込み被いは不要
        try { c.NavigateToString(StartPage.MpvHold(tab.Title)); } catch { }
    }

    /// <summary>mpvプレースホルダーがクリックされた: 元のページへ戻して再生位置もシークする。</summary>
    void MpvReturn(BrowserTab tab)
    {
        var core = tab.View?.CoreWebView2;
        var url = tab.MpvReturnUrl;
        if (core is null || url is null) return;
        var pos = tab.MpvReturnPos;
        tab.MpvReturnUrl = null;
        if (pos > 5) HookResumeSeek(core, pos);
        try { core.Navigate(url); } catch { }
    }

    // ---- 再生画質の上限 (setPlaybackQualityRange: 進捗同期に影響しない本物のプレーヤーAPI) ----

    static readonly string[] QualitySteps = { "hd720", "hd1080", "" };

    static string QualityLabel(string q) => q switch
    {
        "hd720" => "720p",
        "hd1080" => "1080p",
        "hd1440" => "1440p",
        "" => "無制限",
        _ => q,
    };

    async void Quality_Click(object sender, RoutedEventArgs e)
    {
        var i = Array.IndexOf(QualitySteps, _settings.MaxQuality);
        _settings.MaxQuality = QualitySteps[(i + 1) % QualitySteps.Length];
        SettingsStore.Save(_settings);
        RefreshQualityUi();
        // 開いている全タブに即時反映 (注入スクリプトが window.__karuMaxQ を毎tick参照している)
        foreach (var t in Tabs)
        {
            var c = t.View?.CoreWebView2;
            if (c is null) continue;
            try { await c.ExecuteScriptAsync($"window.__karuMaxQ = '{_settings.MaxQuality}'"); }
            catch { }
        }
    }

    void RefreshQualityUi()
        => QualityMenuBtn.Content = $"画質上限: {QualityLabel(_settings.MaxQuality)}（クリックで切替）";

    /// <summary>メモリ使用量の実測をプロセス種別ごとに集計して表示する。</summary>
    async void MemInfo_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        var sb = new StringBuilder();
        long total = 0;
        try
        {
            var host = Process.GetCurrentProcess().WorkingSet64;
            total += host;
            sb.AppendLine($"ホスト(WPF): {host / 1048576.0,8:F1} MB");
            var env = await GetEnvAsync();
            var groups = new Dictionary<string, (int Count, long Bytes)>();
            foreach (var pi in env.GetProcessInfos())
            {
                try
                {
                    var p = Process.GetProcessById(pi.ProcessId);
                    var kind = pi.Kind.ToString();
                    groups.TryGetValue(kind, out var g);
                    groups[kind] = (g.Count + 1, g.Bytes + p.WorkingSet64);
                    total += p.WorkingSet64;
                }
                catch { /* 終了直後のプロセスはスキップ */ }
            }
            foreach (var (kind, g) in groups.OrderByDescending(x => x.Value.Bytes))
                sb.AppendLine($"{kind} ×{g.Count}: {g.Bytes / 1048576.0,8:F1} MB");
            sb.AppendLine($"――――――――――――");
            sb.AppendLine($"合計: {total / 1048576.0,8:F1} MB");
            sb.AppendLine();
            sb.AppendLine("タブ状態:");
            for (int i = 0; i < Tabs.Count; i++)
            {
                var t = Tabs[i];
                var state = t.View is null ? "💤休眠"
                    : t.View.CoreWebView2?.IsSuspended == true ? "⏸停止"
                    : t == _active ? "▶表示" : "・待機";
                var title = t.Title.Length > 28 ? t.Title[..28] + "…" : t.Title;
                sb.AppendLine($"{i + 1}. [{state}] {title}");
            }
        }
        catch (Exception ex) { sb.AppendLine("取得エラー: " + ex.Message); }
        MessageBox.Show(this, sb.ToString(), "Karu メモリ使用量");
    }

    /// <summary>ページ内の動画をフルスクリーン切替 (Ctrl+O)。
    /// requestFullscreen はユーザー操作が必須のため、CDPの Runtime.evaluate を userGesture 付きで使う。</summary>
    async void ToggleVideoFullscreen()
    {
        var core = _active?.View?.CoreWebView2;
        if (core is null) return;
        const string js = """
            (function(){
              var b=document.querySelector('.ytp-fullscreen-button');
              if(b){b.click();return;}
              if(document.fullscreenElement){document.exitFullscreen();return;}
              var v=document.querySelector('video');
              if(v&&v.requestFullscreen)v.requestFullscreen();
            })()
            """;
        var payload = JsonSerializer.Serialize(new { expression = js, userGesture = true });
        try { await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", payload); } catch { }
    }

    void OpenInEdge()
    {
        var url = ActiveUrl();
        if (url.Length == 0) return;
        try
        {
            Process.Start(new ProcessStartInfo("msedge.exe", $"--app={url}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Edge を起動できませんでした。\n" + ex.Message, "Karu");
        }
    }

    // ---- テキストカーソルモード ----

    void Caret_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        ToggleCaretBrowsing();
    }

    void ToggleCaretBrowsing()
    {
        _settings.CaretBrowsing = !_settings.CaretBrowsing;
        SettingsStore.Save(_settings);
        var state = _settings.CaretBrowsing ? "ON" : "OFF";
        var r = MessageBox.Show(this,
            $"テキストカーソルモードを {state} にします。\n反映には再起動が必要です。今すぐ再起動しますか？（タブは引き継がれます）",
            "Karu", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes) RestartApp();
    }
}
