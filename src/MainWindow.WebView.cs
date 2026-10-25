using System.IO;
using System.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Karu;

public partial class MainWindow
{
    Task<CoreWebView2Environment> GetEnvAsync() => _envTask ??= CreateEnvAsync();

    /// <summary>WebView2 環境の生成。再起動(テキストカーソルモード切替)の直後は旧プロセスのブラウザがまだ同じ
    /// プロファイルで生きていて、引数の違う環境は作れない(実測: 生成が失敗する)。少し待って数回やり直す。</summary>
    async Task<CoreWebView2Environment> CreateEnvAsync()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await CoreWebView2Environment.CreateAsync(null, Paths.WebDataDir,
                    new CoreWebView2EnvironmentOptions
                    {
                        // 軽量化フラグ:
                        //   process-per-site + renderer-process-limit=4: プロセス数を抑える。
                        //     ※2まで絞るとWarmTabs+アクティブでプロセス共有が常態化し、遷移先ページが
                        //       稼働中サイト(YouTube等)のレンダラー主スレッドを奪い合って遷移が重くなる
                        //   サイト分離は安全性のため既定で維持。明示的な低メモリ設定時だけ無効化
                        //   disk-cache-size/media-cache-size: ディスクキャッシュを128MB/50MBに制限
                        //   QuickIntensiveWakeUpThrottling...: 非表示ページのタイマーを早期に間引く(サスペンド発動までのつなぎ)
                        //   disable-renderer-accessibility: UIA走査によるアクセシビリティツリー構築を止める(スクリーンリーダー非対応化)
                        //   disable-notifications/speech-api/print-preview: 使わない機能のサービスを起動させない
                        //   disable-component-extensions-with-background-pages: 内蔵コンポーネント拡張の常駐を止める
                        //   disable-features=BackForwardCache: 戻る用のページ保持をやめてメモリ優先
                        //   Prerender2,NoStatePrefetch無効: 「表示していないページ」の投機的な先読み生成を丸ごと止める
                        //   MediaRouter,DialMediaRouteProvider無効: Chromecast探索の常駐を止める
                        //   GlobalMediaControls,LiveCaption無効: メディア操作UI・ライブ字幕MLを止める
                        //   AutofillServerCommunication無効: 入力補完のサーバー照会を止める(ローカル補完は残る)
                        //   SegmentationPlatform無効: 利用予測MLを止める
                        //   SmartScreen/追跡防止も既定で維持。任意サイトを開くブラウザでは保護を優先する
                        //   ※ --enable-low-end-device-mode と --js-flags=--optimize-for-size は、表示中タブの
                        //     V8実行やキャッシュまで遅くしてURL遷移の体感が悪化するため使わない
                        //     (裏タブのメモリはサスペンド/休眠側で回収済みで、これらの追加削減効果は小さい)
                        //
                        // Widevine DRM (U-NEXT/Netflix等の動画配信) を再生するために必要な2点:
                        //   ① msWidevinePlatform: WebView2 は Edge 本体と違い Widevine が既定で無効。
                        //      これを有効にしないと requestMediaKeySystemAccess('com.widevine.alpha') が
                        //      NotSupportedError で落ちる (PlayReady/ClearKey は既定で使えるので気付きにくい)。
                        //   ② --disable-component-update を付けない: Widevine CDM は
                        //      「WidevineCdm コンポーネント」として登録される。実体はランタイム同梱
                        //      (EdgeWebView\Application\<ver>\WidevineCdm) でダウンロードは不要だが、
                        //      component update 自体を切ると登録処理ごと飛ばされて CDM が見つからなくなる。
                        //      ※保護ON時はバックグラウンド通信も維持。保護OFFの旧低メモリ設定時だけ止める。
                        //   ①②どちらか欠けると U-NEXT は「再生エラーが発生しました。（sh_6012）」になる。
                        // 自動化誤判定対策・軽量化・proxyを一か所で構成する。
                        // サイト分離/SmartScreenを切る旧設定は EnableBrowserProtections=false の場合だけ。
                        AdditionalBrowserArguments = BrowserOptions.BuildArguments(_settings),
                        // 拡張(uBlock等)が置かれていないときは拡張基盤ごと起動しない
                        AreBrowserExtensionsEnabled = !_useBuiltinBlock,
                        // Microsoft は任意サイトを開く WebView2 ブラウザでは有効化を推奨している。
                        EnableTrackingPrevention = _settings.EnableBrowserProtections,
                    });
            }
            catch when (attempt < 10) { await Task.Delay(500); }
        }
    }

    /// <summary>%APPDATA%\Karu\extensions 直下の展開済み拡張をプロファイルにインストールする（初回のみ）</summary>
    async Task LoadExtensionsAsync(CoreWebView2Profile profile)
    {
        if (_extensionsLoaded) return;
        _extensionsLoaded = true;
        try
        {
            var dirs = Directory.GetDirectories(Paths.ExtensionsDir);
            if (dirs.Length == 0) return;
            var installed = await profile.GetBrowserExtensionsAsync();
            foreach (var dir in dirs)
            {
                if (!File.Exists(Path.Combine(dir, "manifest.json"))) continue;
                var marker = Path.Combine(dir, ".karu-installed");
                if (File.Exists(marker) &&
                    installed.Any(x => x.Id == File.ReadAllText(marker).Trim()))
                    continue;
                try
                {
                    var ext = await profile.AddBrowserExtensionAsync(dir);
                    File.WriteAllText(marker, ext.Id);
                }
                catch { /* 読み込めない拡張はスキップ */ }
            }
        }
        catch { }
    }

    /// <summary>WebView2の初期化と全イベント配線。新規タブと休眠復帰の両方から使う。
    /// 環境生成の失敗はここから throw され、OnLoaded 側で表示される。</summary>
    async Task<CoreWebView2?> InitCoreAsync(BrowserTab tab, WebView2 view)
    {
        var env = await GetEnvAsync();
        try { await view.EnsureCoreWebView2Async(env); }
        catch when (!Tabs.Contains(tab)) { return null; } // 初期化完了前にタブが閉じられた場合だけ無視

        var core = view.CoreWebView2;
        if (core is null) return null;

        ApplyCoreSettings(core.Settings);
        await LoadExtensionsAsync(core.Profile);
        // document-start注入は1本に結合して一度で登録する (タブ生成・休眠復帰のたびに走るIPCなので
        // 5往復→1往復にして初動を軽くする。各断片は自己完結のIIFEで、";"区切りなら安全に連結できる)
        await core.AddScriptToExecuteOnDocumentCreatedAsync(string.Join("\n;\n", StartupScripts()));
        WireTabEvents(tab, core);
        HookResourceFiltering(core);
        return core;
    }

    static void ApplyCoreSettings(CoreWebView2Settings settings)
    {
        settings.IsPasswordAutosaveEnabled = true;
        settings.IsGeneralAutofillEnabled = true;
        settings.IsStatusBarEnabled = false;
        settings.IsWebMessageEnabled = true; // Vim層のタブ操作コマンド受信に必要
        settings.AreHostObjectsAllowed = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.AreDevToolsEnabled = false;  // F12無効 (CDP経由のCtrl+Oは影響なし)
        settings.IsPinchZoomEnabled = false;
    }

    /// <summary>document-start で全ページに注入するスクリプト群(設定で有無が変わるものを含む)。</summary>
    List<string> StartupScripts()
    {
        var scripts = new List<string> { Injections.LowSpec };
        if (_settings.ForceH264 || _settings.MaxFps > 0)
            scripts.Add(Injections.MediaPolicy(_settings.ForceH264, _settings.MaxFps));
        if (_settings.MaxQuality.Length > 0)
            scripts.Add(Injections.TwitchQuality);
        if (_settings.TwitchAdBlock)
            scripts.Add(Injections.TwitchAdCover);
        scripts.Add(Injections.YouTube(_settings.FocusMode, _settings.MaxQuality, _settings.YouTubeAdSpeedUp, _settings.YouTubeAdCover));
        scripts.Add(Injections.Vim);
        scripts.Add(Injections.PageCover);
        return scripts;
    }

    /// <summary>タブの表示状態(タイトル/読み込み中/全画面)とページからのコマンドをホストへ配線する。</summary>
    void WireTabEvents(BrowserTab tab, CoreWebView2 core)
    {
        core.DocumentTitleChanged += (_, _) =>
        {
            tab.Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "新しいタブ" : core.DocumentTitle;
            if (tab == _active) UpdateWindowTitle();
        };
        core.NewWindowRequested += async (_, args) =>
        {
            args.Handled = true;
            await AddTabAsync(args.Uri);
        };
        core.DownloadStarting += (_, args) =>
        {
            var download = args.DownloadOperation;
            bool finished = false;
            long generation = tab.BeginDownload();
            void Finish()
            {
                if (finished) return;
                finished = true;
                try { download.StateChanged -= OnStateChanged; } catch { }
                tab.EndDownload(generation);
            }
            void OnStateChanged(object? sender, object e)
            {
                if (finished) return;
                try { if (download.State == CoreWebView2DownloadState.InProgress) return; }
                catch { }
                Finish();
            }
            try
            {
                download.StateChanged += OnStateChanged;
                // 極短いダウンロードがハンドラ登録前に完了していた場合も回収する。
                OnStateChanged(download, EventArgs.Empty);
            }
            catch { Finish(); }
        };
        // リンクをクリックした瞬間に炎を出して「処理はもう始まっている」ことを伝える。
        // 中央の半透明カバーはページのDOM内で完結する(PageCover):
        // クリック時は遷移「元」ページがbeforeunloadで自ら出し、新ページ確定後は
        // そのページ自身のPageCoverが操作可能になるまで覆う。
        // WPFで覆う方式はairspace対策のWebView非表示が必須で透過にできないため使わない
        core.NavigationStarting += (_, _) =>
        {
            tab.IsLoading = true;
            if (tab == _active) ShowTitleBarFlame();
        };
        core.NavigationCompleted += (_, args) =>
        {
            tab.IsLoading = false;
            if (tab == _active) HideTitleBarFlame();
            // 中断・失敗時は元ページが残るので、そこに出した被いを引っ込める
            // (成功時は旧ドキュメントごと消えるため不要。新ページの被いはPageCover自身が管理する)
            if (!args.IsSuccess) FireScript(core, "window.__karuCoverHide&&window.__karuCoverHide()");
            // キーボード主体で使うため、読み込み完了時にページへフォーカスを渡す
            if (tab == _active && !IsOverlayOpen) tab.View?.Focus();
        };
        core.WebMessageReceived += (_, args) => OnWebMessage(tab, args);
        // 最後に見た実URL。ブラウザプロセス消失時の復旧とセッション保存の拠り所
        core.SourceChanged += (_, _) =>
        {
            string? s = null;
            try { s = core.Source; } catch { }
            if (!string.IsNullOrEmpty(s) && s != "about:blank") tab.LastUrl = s;
        };
        // WebView2 のプロセス障害。ブラウザプロセス消失は全タブ分のイベントが届くので復旧は一度にまとめる。
        // レンダラー落ちは表示中なら再読み込み、裏なら休眠(URLだけ残して次に開くとき読み直す)
        core.ProcessFailed += (_, e) =>
        {
            switch (e.ProcessFailedKind)
            {
                case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                    RecoverFromBrowserProcessExit();
                    break;
                case CoreWebView2ProcessFailedKind.RenderProcessExited:
                    if (tab == _active) { try { core.Reload(); } catch { } }
                    else HibernateTab(tab);
                    break;
            }
        };
        // 非アクティブタブ(裏で全画面のまま切替済み等)のイベントで窓の状態を動かさない
        core.ContainsFullScreenElementChanged += (_, _) =>
        {
            if (tab != _active) return;
            _fsFromElement = core.ContainsFullScreenElement;
            SetFullscreen(core.ContainsFullScreenElement);
        };
    }

    /// <summary>リクエスト単位の遮断・差し替え(先読み/広告/Twitch広告)と Save-Data ヘッダ。</summary>
    void HookResourceFiltering(CoreWebView2 core)
    {
        // 「表示しないデータを受信しない」レイヤー:
        //   - Sec-Purpose/Purpose: prefetch が付いた投機的リクエストを遮断 (Prerender2無効化の取りこぼし対策)
        //   - Save-Data: on を送り、対応サイトには軽量版レスポンスを要求
        //   - 拡張が無ければ blocklist.txt による広告・トラッカー遮断もここで行う
        // フィルタ対象のリクエストは1件ずつネットワーク処理が止まってUIスレッドへCOM往復するため、
        // 「何を対象にするか」がそのままページ読み込みの速度に効く。ナビゲーション本体(Document)は
        // 件数が少ないので常に見て、サブリソースは "*" ではなくブロック対象ドメインの
        // ワイルドカードだけを登録する (AdBlocker.UriFilters)。
        core.WebResourceRequested += (_, args) =>
        {
            var req = args.Request;
            bool prefetch = false;
            try
            {
                prefetch = (req.Headers.Contains("Sec-Purpose") &&
                            req.Headers.GetHeader("Sec-Purpose").Contains("prefetch")) ||
                           (req.Headers.Contains("Purpose") &&
                            req.Headers.GetHeader("Purpose") == "prefetch");
            }
            catch { }
            if (prefetch || (_useBuiltinBlock && _adblock.ShouldBlock(req.Uri)))
            {
                args.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked by Karu", "");
                return;
            }
            if (_settings.TwitchAdBlock &&
                (TwitchAds.IsUsher(req.Uri) || TwitchAds.IsMediaPlaylist(req.Uri)))
            {
                HandleTwitchPlaylist(core, args);
                return;
            }
            try { req.Headers.SetHeader("Save-Data", "on"); } catch { }
        };
        // SourceKinds.All にすると Service Worker / Shared Worker 発のリクエストも対象になる
        // (既定の Document のみでは SW 経由の広告・計測が素通りする)
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Document,
            CoreWebView2WebResourceRequestSourceKinds.All);
        if (_useBuiltinBlock)
            foreach (var pattern in _adblock.UriFilters())
                core.AddWebResourceRequestedFilter(pattern, CoreWebView2WebResourceContext.All,
                    CoreWebView2WebResourceRequestSourceKinds.All);
        // Twitch の SSAI 対策 (TwitchAds)。HLS の取得は Web Worker 内で走るのでページ側の
        // fetch フックからは見えないが、ここなら XmlHttpRequest として捕捉できる (実測確認済み)
        if (_settings.TwitchAdBlock)
            foreach (var pattern in new[] { "*usher.ttvnw.net/api/*", "*.playlist.ttvnw.net/v1/playlist/*" })
                core.AddWebResourceRequestedFilter(pattern, CoreWebView2WebResourceContext.All,
                    CoreWebView2WebResourceRequestSourceKinds.All);
    }

    /// <summary>
    /// Twitch のプレイリスト取得を横取りして、広告入りなら取り直した中身に差し替える。
    /// ネットワーク往復が要るので必ず deferral を取る (取らずに await するとUIスレッドを止めてしまう)。
    /// 取得に失敗したときは Response を設定せずに完了させ、通常どおりネットワークへ流す。
    /// </summary>
    async void HandleTwitchPlaylist(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var uri = args.Request.Uri;
            string? body;
            bool blocked = false;
            if (TwitchAds.IsUsher(uri))
            {
                body = await _twitchAds.InterceptUsherAsync(uri);
            }
            else
            {
                var r = await _twitchAds.InterceptPlaylistAsync(uri);
                body = r?.Body;
                blocked = r?.Blocked ?? false;
            }
            // 差し替えられなかった広告はページ側で消音+被いに回す
            FireScript(core, $"window.__karuTwitchAd&&window.__karuTwitchAd({Injections.JsBool(blocked)})");
            if (body is null) return;

            var bytes = new MemoryStream(Encoding.UTF8.GetBytes(body));
            args.Response = core.Environment.CreateWebResourceResponse(
                bytes, 200, "OK",
                "Content-Type: application/vnd.apple.mpegurl\r\nAccess-Control-Allow-Origin: *");
        }
        catch { }
        finally { deferral.Complete(); }
    }
}
