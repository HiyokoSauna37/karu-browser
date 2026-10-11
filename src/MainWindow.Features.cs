using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Karu;

/// <summary>機能面: 動画集中モード・画質上限・mpv/Edge連携・翻訳・メニュー・メモリ表示。
/// オーバーレイ(URL入力/タブ一覧/お気に入り)は MainWindow.Overlays.cs。</summary>
public partial class MainWindow
{
    // ---- お気に入りメニュー ----

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
        await BroadcastScriptAsync(_settings.FocusMode ? Injections.ToggleFocusOn : Injections.ToggleFocusOff);
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
        try { ExternalProcess.Start("explorer.exe", Paths.AppDataDir); } catch { }
    }

    void RefreshNetworkUi() => NetworkMenuBtn.Content = NetworkRouting.StatusText(_settings);

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
        if (NetworkRouting.IsProxyEnabled(_settings))
        {
            MessageBox.Show(this,
                "Karu のプロキシ使用中は、外部 mpv の通信経路を保証できないため引き渡しを無効にしています。\n" +
                "VPNなどシステム全体の経路を使う場合は ProxyServer を空にしてください。", "Karu");
            return;
        }
        if (Mpv.IsDrmSite(url))
        {
            MessageBox.Show(this,
                "このサイトはDRM保護のため mpv では再生できません。\nKaru のプレーヤーでそのまま再生してください。", "Karu");
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
        await BroadcastScriptAsync($"window.__karuMaxQ = {JsonSerializer.Serialize(_settings.MaxQuality)}");
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
            using var hostProcess = Process.GetCurrentProcess();
            var host = hostProcess.WorkingSet64;
            total += host;
            sb.AppendLine($"ホスト(WPF): {host / 1048576.0,8:F1} MB");
            var env = await GetEnvAsync();
            var groups = new Dictionary<string, (int Count, long Bytes)>();
            foreach (var pi in env.GetProcessInfos())
            {
                try
                {
                    using var p = Process.GetProcessById(pi.ProcessId);
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
                var state = StateOf(t) switch
                {
                    TabState.Hibernated => "💤休眠",
                    TabState.Suspended => "⏸停止",
                    TabState.Active => "▶表示",
                    _ => "・待機",
                };
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

    /// <summary>
    /// YouTube注入スクリプトからの依頼で、広告スキップボタンの座標に本物のクリックを打つ。
    /// YouTubeのスキップボタンは isTrusted=false の合成クリックを広告ブロッカーとして記録したうえで無視する
    /// ため、ページ内JSでは押せない。ブラウザ側から CDP の Input.dispatchMouseEvent で入力を注入すると、
    /// ユーザー操作と区別のつかないイベントになる。座標はCSSピクセル(ビューポート基準)で、
    /// getBoundingClientRect の値がそのまま使える(DPIスケール換算は不要)。
    /// 可視判定とおとり回避はスクリプト側(<see cref="Injections.YouTube"/>)、ここは最後の砦として
    /// 「YouTube上のタブ」「整数座標」「連打抑止」だけを確認する。
    /// </summary>
    async Task TrustedAdSkipClickAsync(BrowserTab tab, string cmd)
    {
        var core = tab.View?.CoreWebView2;
        if (core is null) return;
        if (!YouTubeAdSkip.TryParseCommand(cmd, out var x, out var y)) return;
        // 注入スクリプトはYouTube以外では送らないが、別サイトへ本物のクリックを打たないようホスト名でも縛る
        if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) || !YouTubeAdSkip.IsYouTubeHost(uri.Host)) return;
        var now = DateTime.UtcNow;
        if (now - tab.LastAdSkipClick < TimeSpan.FromMilliseconds(300)) return;
        tab.LastAdSkipClick = now;
        try
        {
            foreach (var json in YouTubeAdSkip.ClickSequence(x, y))
            {
                // 非表示WebView(背景タブ)では呼び出しの一つが完了しないことがあるが、入力自体は届く
                // (実測: 押下か解放のどちらかが返らなくてもクリックは発火する)。完了を待ちすぎず、
                // 返らなくても次(解放)を送る。待ち続けるとこのタブの以後のスキップが詰まる
                var call = core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", json);
                await Task.WhenAny(call, Task.Delay(1000));
            }
        }
        catch { }
    }

    void OpenInEdge()
    {
        var url = ActiveUrl();
        if (url.Length == 0) return;
        if (NetworkRouting.IsProxyEnabled(_settings))
        {
            MessageBox.Show(this,
                "Karu のプロキシ設定は外部 Edge へ引き継げないため、この操作を無効にしています。\n" +
                "直接接続によるIP漏えいを避けるためです。", "Karu");
            return;
        }
        try { ExternalProcess.Start("msedge.exe", "--app=" + url); }
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

    // ---- タブの別ウィンドウ分離 (Ctrl+Shift+D) ----

    /// <summary>現在のタブを新しいウィンドウ(別プロセス)へ分離する。
    /// WebView2 には既存タブを別ウィンドウへ移す API が無いため、そのタブの URL で新しい Karu を
    /// 起動し(同じ WebDataDir を同時共有できるのは実測確認済み)、元タブを閉じる。引き継ぐのは URL のみ
    /// (ページ内の入力・履歴・スクロール位置は失われる)。</summary>
    void DetachTab(BrowserTab tab)
    {
        if (ContentUrl(tab) is not { } url) return; // 中身の無いタブは分離しない
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        // 分離窓は終了時にセッションを上書きせず合流させる(App.DetachedFlag)
        try { ExternalProcess.Start(exe, App.DetachedFlag, url); }
        catch { return; }
        CloseTab(tab); // 新ウィンドウを起動してから元タブを閉じる
    }

    // ---- ページ翻訳 (Ctrl+Shift+Y / メニュー。「このページを翻訳」相当) ----

    record TransState(bool Exists, string Shown, bool HasTranslated, bool Changed);
    record TransCollect(int Gen, string[] Segs);
    static readonly JsonSerializerOptions TransJsonOpts = new() { PropertyNameCaseInsensitive = true };
    readonly HashSet<BrowserTab> _translating = new(); // 翻訳中の再トグルを無視する(適用と復元の競合防止)

    void Translate_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        if (_active is not null) _ = TranslatePageToggleAsync(_active);
    }

    void Detach_Click(object sender, RoutedEventArgs e)
    {
        MenuPopup.IsOpen = false;
        if (_active is not null) DetachTab(_active);
    }

    /// <summary>ページ翻訳のトグル。1回目=本文を日本語へ in-place 翻訳、2回目=原文へ戻す
    /// (再翻訳・復元はキャッシュ利用でネットワーク不要)。ネットワーク(gtx)はホスト側 HttpClient で
    /// 行い(ページ内 fetch は CORS で弾かれるため)、DOM 走査・置換は注入 JS に任せる。
    /// 翻訳は届いたバッチから順次適用し、最後に全訳文で確定適用する(訳キャッシュもそこで確定)。</summary>
    async Task TranslatePageToggleAsync(BrowserTab tab)
    {
        var core = tab.View?.CoreWebView2;
        if (core is null) return;
        if (!_translating.Add(tab)) return;
        try
        {
            var stateJson = await core.ExecuteScriptAsync(Injections.TranslateState);
            var st = JsonSerializer.Deserialize<TransState>(stateJson, TransJsonOpts);
            // キャッシュでのトグルは「ページが実質変わっていない」場合だけ意味がある。
            // SPA のソフト遷移で changed=true になったら、shown フラグは信用せず新規翻訳へ落とす
            // (旧: 遷移後も restore/reapply に入り、切り離された旧要素へ空振りして「効かない」ように見えた)。
            // 翻訳表示中 → 原文へ戻す
            if (st is { Exists: true, Changed: false, Shown: "translated" })
            {
                await core.ExecuteScriptAsync(Injections.TranslateRestore);
                return;
            }
            // 原文表示 + 訳キャッシュ有り → ネットワーク無しで再適用
            if (st is { Exists: true, Changed: false, Shown: "original", HasTranslated: true })
            {
                await core.ExecuteScriptAsync(Injections.TranslateReapply);
                return;
            }
            // 新規翻訳(SPA 遷移で別ページ扱いになった場合を含む)。画面に旧訳が残っていれば
            // 先に原文へ戻してから収集する(訳文を「原文」として再収集して二重翻訳しない)
            if (st is { Exists: true, Shown: "translated" })
                await core.ExecuteScriptAsync(Injections.TranslateRestore);
            // テキストを集める → gtx で並列翻訳 → 届いたバッチから順次適用
            var colJson = await core.ExecuteScriptAsync(Injections.TranslateCollect);
            var col = JsonSerializer.Deserialize<TransCollect>(colJson, TransJsonOpts);
            if (col is null || col.Segs.Length == 0) return;
            async Task ApplyAsync(string[] cur, bool final)
            {
                var transJson = JsonSerializer.Serialize(cur);
                // gen 付きで適用 — API 往復中に再収集・ページ遷移していたら ApplyFn 側が捨てる
                await core.ExecuteScriptAsync($"{Injections.TranslateApplyFn}({transJson},{col.Gen},{Injections.JsBool(final)})");
            }
            var throttle = Stopwatch.StartNew();
            var (translations, _, _) = await Translator.TranslateAsync(_translateHttp, col.Segs, "ja", onProgress: cur =>
            {
                if (throttle.ElapsedMilliseconds < 350) return Task.CompletedTask; // 適用は DOM 全体の組み直しなので間引く
                throttle.Restart();
                return ApplyAsync(cur, final: false);
            });
            await ApplyAsync(translations, final: true);
        }
        catch { }
        finally { _translating.Remove(tab); }
    }
}
