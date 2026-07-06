using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Karu;

/// <summary>
/// タブの切替・破棄・ナビゲーションと3段階メモリライフサイクルの管理。
///   1. Active    : 表示中。フルメモリ。
///   2. Suspended : 非表示15秒。WebView2のTrySuspendでレンダラーのメモリを解放。
///   3. Hibernated: 非表示が続いたタブ。WebViewを完全破棄し、URL・タイトル・favicon・動画位置のみ保持(数百バイト)。
///      復帰時はWebViewを再生成して動画位置へ自動シーク。
/// さらに空き物理メモリが閾値を切ると全非表示タブを緊急休眠する。
/// </summary>
public partial class MainWindow
{
    bool _maintaining;

    void ActivateTab(BrowserTab tab)
    {
        if (_active is not null && _active != tab)
        {
            _active.HiddenAt = DateTime.Now;
            SetMemoryTarget(_active, low: true);
        }
        _active = tab;
        if (tab.View is null) _ = WakeTabAsync(tab); // 休眠から復帰 (View自体は同期的に生成される)
        foreach (var t in Tabs)
        {
            t.IsActive = t == tab;
            if (t.View is not null)
                t.View.Visibility = t == tab ? Visibility.Visible : Visibility.Hidden;
        }
        var core = tab.View?.CoreWebView2;
        if (core?.IsSuspended == true) core.Resume();
        SetMemoryTarget(tab, low: false);
        // キーボード主体のため、タブ切替時は即ページへフォーカス
        if (!IsOverlayOpen) tab.View?.Focus();
        // 切替先タブの読み込み状態にタイトルバーの炎を合わせる (中央の被いはページDOM内なので自動で付いてくる)
        if (tab.IsLoading) ShowTitleBarFlame(); else HideTitleBarFlame();
        // 要素全画面のタブから切替/閉鎖したとき、離脱イベントは飛んでこない(破棄時は特に)ので
        // ここで新タブの状態に合わせて解除する。F11手動全画面はタブに紐付かないので触らない
        if (_fullscreen && _fsFromElement)
        {
            bool fs = false;
            try { fs = tab.View?.CoreWebView2?.ContainsFullScreenElement == true; } catch { }
            if (!fs) { _fsFromElement = false; SetFullscreen(false); }
        }
        UpdateWindowTitle();
    }

    /// <summary>非表示タブの描画メモリ解放をブラウザに指示する。音声再生中は音切れ防止のため通常のまま。</summary>
    static void SetMemoryTarget(BrowserTab tab, bool low)
    {
        try
        {
            var core = tab.View?.CoreWebView2;
            if (core is null) return;
            core.MemoryUsageTargetLevel = low && !core.IsDocumentPlayingAudio
                ? CoreWebView2MemoryUsageTargetLevel.Low
                : CoreWebView2MemoryUsageTargetLevel.Normal;
        }
        catch { }
    }

    /// <summary>15秒ごとにタブのライフサイクルを進める (Active → Suspended → Hibernated)。</summary>
    async void MaintainTabs()
    {
        if (_maintaining) return;
        _maintaining = true;
        try
        {
            bool minimized = WindowState == WindowState.Minimized;
            bool pressure = SystemMemory.AvailableGB() < _settings.PressureAvailableGB;
            var now = DateTime.Now;

            // 直近に使った順。上位 WarmTabs 個は「温存」枠として休眠を遅らせる
            var hidden = Tabs.Where(t => t != _active).OrderByDescending(t => t.HiddenAt).ToList();
            for (int i = 0; i < hidden.Count; i++)
            {
                var t = hidden[i];
                if (t.View is null) continue; // 休眠済み
                CoreWebView2? core = null;
                try { core = t.View.CoreWebView2; } catch { } // ループ中のawaitの間に閉じられた
                if (core is null) continue;   // 初期化中 or 破棄済み
                if (core.IsDocumentPlayingAudio) continue; // BGM再生中のタブは絶対に触らない

                double idleSec = (now - t.HiddenAt).TotalSeconds;
                double hibernateAfter = pressure ? 30
                    : i < _settings.WarmTabs ? _settings.HibernateWarmMinutes * 60
                    : _settings.HibernateColdSeconds;

                if (idleSec >= hibernateAfter)
                {
                    if (!core.IsSuspended) await CaptureVideoPosAsync(t); // サスペンド済みなら記録済み
                    HibernateTab(t);
                }
                else if (!core.IsSuspended && idleSec >= 15)
                {
                    await CaptureVideoPosAsync(t); // 休眠に備えて動画位置を先に記録
                    try { await core.TrySuspendAsync(); } catch { }
                }
            }

            // 最小化中のアクティブタブはサスペンドまで (復帰の速さを優先して休眠はしない)
            if (minimized && _active?.View?.CoreWebView2 is { } ac
                && !ac.IsSuspended && !ac.IsDocumentPlayingAudio
                && (now - _minimizedAt).TotalSeconds >= 15)
            {
                await CaptureVideoPosAsync(_active);
                try { await ac.TrySuspendAsync(); } catch { }
            }

            // パーキングモード: 最小化が続いたらアクティブ含め全タブ休眠 (音声再生中は除外)
            if (minimized && (now - _minimizedAt).TotalMinutes >= _settings.ParkMinutes)
            {
                foreach (var t in Tabs.ToList())
                {
                    CoreWebView2? c = null;
                    try { c = t.View?.CoreWebView2; } catch { }
                    if (c is null || c.IsDocumentPlayingAudio) continue;
                    if (!c.IsSuspended) await CaptureVideoPosAsync(t);
                    HibernateTab(t, allowActive: true);
                }
            }
        }
        finally { _maintaining = false; }
    }

    /// <summary>動画の再生位置を記録する。サスペンド/休眠の直前に呼ぶ。</summary>
    async Task CaptureVideoPosAsync(BrowserTab tab)
    {
        var core = tab.View?.CoreWebView2;
        if (core is null) return;
        try
        {
            var r = await core.ExecuteScriptAsync(
                "(function(){var v=document.querySelector('video');return v?(v.currentTime||0):0})()");
            double.TryParse(r.Trim('"'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var p);
            tab.LastVideoPos = p;
        }
        catch { }
    }

    /// <summary>タブを休眠させる。WebViewを完全破棄し、URLと動画位置だけ残す。</summary>
    void HibernateTab(BrowserTab tab, bool allowActive = false)
    {
        var view = tab.View;
        if (view is null || (tab == _active && !allowActive)) return;
        string? url = null;
        try { url = view.CoreWebView2?.Source; } catch { }
        if (url == "about:blank") url = null;
        // mpvプレースホルダー(NavigateToString)中なら、復帰先として元動画のURL/位置を引き継ぐ
        if (url is null && tab.MpvReturnUrl is not null)
        {
            url = tab.MpvReturnUrl;
            tab.LastVideoPos = tab.MpvReturnPos;
        }
        WebHost.Children.Remove(view);
        try { view.Dispose(); } catch { }
        tab.Detach(url, tab.LastVideoPos);
    }

    /// <summary>休眠タブを復帰させる。WebViewを再生成し、URLを開き直して動画位置へシークする。</summary>
    async Task WakeTabAsync(BrowserTab tab)
    {
        var url = tab.SleepUrl;
        var pos = tab.SleepVideoPos;
        var view = NewView();
        tab.Attach(view);
        WebHost.Children.Add(view);
        view.Visibility = tab == _active ? Visibility.Visible : Visibility.Hidden;

        var core = await InitCoreAsync(tab, view);
        if (core is null) return;
        if (url is null) { ShowStartPage(tab); return; }
        if (pos > 5) HookResumeSeek(core, pos);
        try { core.Navigate(url); } catch { }
    }

    /// <summary>復帰後の最初のナビゲーション完了時に、記録していた再生位置へシークする。</summary>
    static void HookResumeSeek(CoreWebView2 core, double pos)
    {
        var posStr = pos.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            core.NavigationCompleted -= Handler;
            _ = core.ExecuteScriptAsync(
                "(function(){var t=" + posStr + ",n=0,id=setInterval(function(){" +
                "var v=document.querySelector('video');" +
                "if(v&&v.readyState>0){v.currentTime=t;clearInterval(id);return;}" +
                "if(++n>40)clearInterval(id);},250);})()");
        }
        core.NavigationCompleted += Handler;
    }

    void CloseTab(BrowserTab tab)
    {
        int idx = Tabs.IndexOf(tab);
        if (idx < 0) return;
        var url = TabUrl(tab);
        if (!string.IsNullOrEmpty(url) && url != "about:blank")
        {
            _closedUrls.Add(url);
            if (_closedUrls.Count > 20) _closedUrls.RemoveAt(0); // 復元用の履歴は直近20件で十分
        }
        bool wasActive = tab == _active;
        if (wasActive) _active = null; // 破棄済みビューを ActivateTab / StateChanged が触らないように先に外す
        Tabs.Remove(tab);
        if (tab.View is not null)
        {
            WebHost.Children.Remove(tab.View);
            try { tab.View.Dispose(); } catch { }
            // View を null にしておかないと、MaintainTabs のループが await 中に閉じたタブを
            // 「生きているタブ」としてスナップショットから触り ObjectDisposedException になる
            tab.Detach(null, 0);
        }
        // 最後のタブを閉じてもアプリは終了せず、新しいスタートページを開く (終了は Ctrl+Shift+W か ✕)
        if (Tabs.Count == 0) { _ = AddTabAsync(null); return; }
        if (wasActive) ActivateTab(Tabs[Math.Min(idx, Tabs.Count - 1)]);
        else UpdateWindowTitle(); // タイトルの [位置/総数] を更新
    }

    async void RestoreClosedTab()
    {
        if (_closedUrls.Count == 0) return;
        var url = _closedUrls[^1];
        _closedUrls.RemoveAt(_closedUrls.Count - 1);
        await AddTabAsync(url);
    }

    void MoveTab(int dir)
    {
        if (_active is null) return;
        int i = Tabs.IndexOf(_active);
        int j = i + dir;
        if (i < 0 || j < 0 || j >= Tabs.Count) return;
        Tabs.Move(i, j);
        UpdateWindowTitle();
    }

    void JumpTab(int idx)
    {
        if (idx >= 0 && idx < Tabs.Count) ActivateTab(Tabs[idx]);
    }

    void CycleTab(int dir)
    {
        if (Tabs.Count < 2 || _active is null) return;
        int idx = (Tabs.IndexOf(_active) + dir + Tabs.Count) % Tabs.Count;
        ActivateTab(Tabs[idx]);
    }

    void Navigate(BrowserTab tab, string input)
    {
        var core = tab.View?.CoreWebView2;
        if (core is null) return;
        try { core.Navigate(UrlUtil.Normalize(input)); }
        catch { core.Navigate("https://www.google.com/search?q=" + Uri.EscapeDataString(input)); }
    }

    void ShowStartPage(BrowserTab tab)
        => tab.View?.CoreWebView2?.NavigateToString(StartPage.Generate(Bookmarks));

    string ActiveUrl()
    {
        var s = _active is null ? "" : TabUrl(_active) ?? "";
        return s == "about:blank" ? "" : s;
    }

    /// <summary>タブバーが無いため、ウィンドウタイトルに [位置/総数] を出して現在地を示す。</summary>
    void UpdateWindowTitle()
    {
        var t = _active?.Title;
        var pos = Tabs.Count > 1 && _active is not null
            ? $"[{Tabs.IndexOf(_active) + 1}/{Tabs.Count}] " : "";
        Title = string.IsNullOrWhiteSpace(t) || t == "新しいタブ"
            ? pos + "Karu" : pos + t + " - Karu";
    }
}
