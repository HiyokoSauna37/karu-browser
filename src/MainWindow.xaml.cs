using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Karu;

/// <summary>本体。ライフサイクル・WebView2環境・タブ生成・ウィンドウ操作を担当。
/// タブ切替/休眠は MainWindow.Tabs.cs、機能系(mpv/お気に入り/集中モード)は MainWindow.Features.cs。</summary>
public partial class MainWindow : Window
{
    public ObservableCollection<BrowserTab> Tabs { get; } = new();
    public ObservableCollection<Bookmark> Bookmarks => _bookmarks.Items;

    readonly BookmarkStore _bookmarks = new();
    readonly AdBlocker _adblock = new();
    readonly AppSettings _settings = SettingsStore.Load();
    readonly TwitchAds _twitchAds;
    readonly HttpClient _translateHttp;
    readonly DispatcherTimer _sleepTimer;
    readonly List<string> _closedUrls = new();
    readonly bool _useBuiltinBlock; // 拡張(uBlock)が無いときだけ簡易ブロッカーを使う
    Task<CoreWebView2Environment>? _envTask;
    BrowserTab? _active;
    bool _extensionsLoaded;
    bool _fullscreen;
    bool _fsFromElement; // 現在の全画面がHTML要素由来か (F11手動と区別し、タブ切替時の自動解除判定に使う)
    WindowState _preFsState = WindowState.Normal;
    DateTime _minimizedAt = DateTime.Now;

    static readonly Brush StarOffBrush = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));
    static readonly Brush StarOnBrush = new SolidColorBrush(Color.FromRgb(0xD4, 0x84, 0x5E));

    // ナビゲーション中の炎アニメーション (2フレームを交互表示。クリック直後に「もう始まっている」を伝える)
    static readonly BitmapImage FlameFrame1 = new(new Uri("pack://application:,,,/karu-flame1.png"));
    static readonly BitmapImage FlameFrame2 = new(new Uri("pack://application:,,,/karu-flame2.png"));
    readonly DispatcherTimer _flameTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    bool _flameToggle;

    public MainWindow()
    {
        // WebView2外の通信もブラウザと同じプロキシ経路へ揃える。
        _twitchAds = new TwitchAds(NetworkRouting.CreateHttpClient(_settings, TimeSpan.FromSeconds(8)));
        _translateHttp = NetworkRouting.CreateHttpClient(_settings, TimeSpan.FromSeconds(20));
        InitializeComponent();
        // 150%スケーリング環境などで「元のサイズ」が画面からはみ出さないように制限
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(Width, wa.Width * 0.96);
        Height = Math.Min(Height, wa.Height * 0.94);
        DataContext = this;
        _useBuiltinBlock = !Directory.EnumerateDirectories(Paths.ExtensionsDir)
            .Any(d => File.Exists(Path.Combine(d, "manifest.json")));
        RefreshFocusUi();
        RefreshQualityUi();
        RefreshNetworkUi();
        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        StateChanged += (_, _) =>
        {
            UpdateRootMargin();
            if (WindowState == WindowState.Minimized)
            {
                // 最小化中はWebViewを非表示にしてサスペンド可能にし、描画メモリの解放も指示する
                _minimizedAt = DateTime.Now;
                foreach (var t in Tabs)
                {
                    if (t.View is not null) t.View.Visibility = Visibility.Hidden;
                    SetMemoryTarget(t, low: true);
                }
            }
            else if (_active is not null)
            {
                _parked = false; // 次にパーキングへ入ったらまた作業セットを返す
                if (_active.View is null) _ = WakeTabAsync(_active); // パーキング休眠からの復帰
                else
                {
                    _active.View.Visibility = Visibility.Visible;
                    var c = _active.View.CoreWebView2;
                    if (c?.IsSuspended == true) c.Resume();
                }
                SetMemoryTarget(_active, low: false);
            }
        };
        _sleepTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _sleepTimer.Tick += (_, _) => MaintainTabs();
        _sleepTimer.Start();
        _flameTimer.Tick += (_, _) =>
        {
            _flameToggle = !_flameToggle;
            TitleBarFlame.Source = _flameToggle ? FlameFrame2 : FlameFrame1;
        };
    }

    void ShowTitleBarFlame()
    {
        if (TitleBarFlame.Visibility == Visibility.Visible) return;
        TitleBarFlame.Source = FlameFrame1;
        _flameToggle = false;
        TitleBarFlame.Visibility = Visibility.Visible;
        _flameTimer.Start();
    }

    void HideTitleBarFlame()
    {
        _flameTimer.Stop();
        TitleBarFlame.Visibility = Visibility.Collapsed;
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _bookmarks.Load();
        _adblock.Load();
        if (NetworkRouting.HasInvalidProxy(_settings))
            MessageBox.Show(this,
                "settings.json の ProxyServer の書式が不正です。プロキシなし(直接接続)で動作しています。\n" +
                "http://host:port、https://host:port、socks5://host:port のいずれかで指定してください(認証情報の埋め込みは不可)。",
                "Karu", MessageBoxButton.OK, MessageBoxImage.Warning);
        var urls = ((App)Application.Current).LaunchUrls;
        if (urls.Length == 0) urls = SessionStore.Load();
        // 壊れた/手編集されたセッションで無制限にタブを生成しない。
        urls = urls.Where(u => !string.IsNullOrWhiteSpace(u)).TakeLast(100).ToArray();
        try
        {
            if (urls.Length == 0) await AddTabAsync(null);
            else
            {
                // 全セッションタブの WebView を一斉生成すると起動時だけRAMが跳ねる。
                // 従来どおり最後のタブだけを表示し、それ以前は最初から休眠状態で復元する。
                foreach (var u in urls[..^1]) AddSleepingTab(u);
                await AddTabAsync(urls[^1]);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "WebView2 の初期化に失敗しました。\n" + ex.Message, "Karu",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _sleepTimer.Stop();
        _flameTimer.Stop();
        SaveSessionNow();
        foreach (var t in Tabs) t.View?.Dispose();
        _twitchAds.Dispose();
        _translateHttp.Dispose();
    }

    /// <summary>開いているタブをセッションファイルへ書く。通常終了のほか、未処理例外の直後にも呼ばれる。
    /// 分離ウィンドウは自分のタブで上書きせず合流させる(最後に閉じた窓が他の窓のタブを消さないように)。</summary>
    public void SaveSessionNow()
    {
        var urls = Tabs.Select(t => TabUrl(t) ?? "").Where(u => u.Length > 0 && u != "about:blank");
        if (((App)Application.Current).IsDetachedWindow) SessionStore.Append(urls);
        else SessionStore.Save(urls);
    }

    /// <summary>タブの現在URL。休眠中は保持しているURLを返す。</summary>
    static string? TabUrl(BrowserTab t)
    {
        if (t.View is null) return t.SleepUrl;
        string? url;
        try { url = t.View.CoreWebView2?.Source; } catch { url = null; }
        // mpvプレースホルダー(NavigateToString=about:blank)中は元動画のURLを実体として扱う
        // (これが無いとセッション保存・閉じタブ復元からmpv再生中のタブが消える)
        if (url == "about:blank" && t.MpvReturnUrl is not null) return t.MpvReturnUrl;
        // 初期化前・ブラウザプロセス消失後(Source が about:blank に化ける/取れない)は最後に見た実URLへ戻す。
        // これが無いと、落ちたブラウザのタブは「空タブ」として捨てられ、終了時にセッションごと消える
        if (string.IsNullOrEmpty(url) || url == "about:blank") return t.LastUrl ?? url;
        return url;
    }

    /// <summary>結果を待たないスクリプト実行。破棄済み/消失後の WebView では例外になるが、その Task を観測しないと
    /// finalizer スレッドの UnobservedTaskException として crash.log に残るだけなので、ここで受け止めて捨てる。</summary>
    static async void FireScript(CoreWebView2 core, string js)
    {
        try { await core.ExecuteScriptAsync(js); } catch { }
    }

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

        core.Settings.IsPasswordAutosaveEnabled = true;
        core.Settings.IsGeneralAutofillEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsWebMessageEnabled = true; // Vim層のタブ操作コマンド受信に必要
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsSwipeNavigationEnabled = false;
        core.Settings.AreDevToolsEnabled = false;  // F12無効 (CDP経由のCtrl+Oは影響なし)
        core.Settings.IsPinchZoomEnabled = false;
        await LoadExtensionsAsync(core.Profile);
        // document-start注入は1本に結合して一度で登録する (タブ生成・休眠復帰のたびに走るIPCなので
        // 5往復→1往復にして初動を軽くする。各断片は自己完結のIIFEで、";"区切りなら安全に連結できる)
        var scripts = new List<string> { Injections.LowSpec };
        if (_settings.ForceH264 || _settings.MaxFps > 0)
            scripts.Add(Injections.MediaPolicy(_settings.ForceH264, _settings.MaxFps));
        if (_settings.MaxQuality.Length > 0)
            scripts.Add(Injections.TwitchQuality);
        if (_settings.TwitchAdBlock)
            scripts.Add(Injections.TwitchAdCover);
        scripts.Add(Injections.YouTube(_settings.FocusMode, _settings.MaxQuality, _settings.YouTubeAdSeek));
        scripts.Add(Injections.Vim);
        scripts.Add(Injections.PageCover);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(string.Join("\n;\n", scripts));

        core.DocumentTitleChanged += (_, _) =>
        {
            tab.Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "新しいタブ" : core.DocumentTitle;
            if (tab == _active) UpdateWindowTitle();
        };
        core.FaviconChanged += async (_, _) =>
        {
            try
            {
                using var src = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
                if (src is null) { tab.Favicon = null; return; }
                using var ms = new MemoryStream();
                await src.CopyToAsync(ms);
                if (ms.Length == 0) { tab.Favicon = null; return; }
                ms.Position = 0;
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = ms;
                img.EndInit();
                img.Freeze();
                tab.Favicon = img;
            }
            catch { tab.Favicon = null; }
        };
        core.NewWindowRequested += async (_, args) =>
        {
            args.Handled = true;
            await AddTabAsync(args.Uri);
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

        return core;
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
            FireScript(core, $"window.__karuTwitchAd&&window.__karuTwitchAd({(blocked ? "true" : "false")})");
            if (body is null) return;

            var bytes = new MemoryStream(Encoding.UTF8.GetBytes(body));
            args.Response = core.Environment.CreateWebResourceResponse(
                bytes, 200, "OK",
                "Content-Type: application/vnd.apple.mpegurl\r\nAccess-Control-Allow-Origin: *");
        }
        catch { }
        finally { deferral.Complete(); }
    }

    /// <summary>注入スクリプト(Vim層・ブックマークオーバーレイ)からのタブ操作コマンドを処理する。</summary>
    void OnWebMessage(BrowserTab tab, CoreWebView2WebMessageReceivedEventArgs args)
    {
        string? raw = null;
        try { raw = args.TryGetWebMessageAsString(); } catch { }
        // 注入スクリプト由来の証明としてトークン接頭辞を検証する。
        // これが無いと任意のページのJSがタブ操作コマンドを偽装できてしまう。
        if (raw is null || !raw.StartsWith(Injections.MessageToken + "|", StringComparison.Ordinal)) return;
        var cmd = raw[(Injections.MessageToken.Length + 1)..];
        // YouTube広告スキップ: 座標付きコマンドなので switch の前で処理する
        if (cmd.StartsWith(YouTubeAdSkip.Command + "|", StringComparison.Ordinal)) { _ = TrustedAdSkipClickAsync(tab, cmd); return; }
        switch (cmd)
        {
            case "tabClose": CloseTab(tab); break;
            case "tabRestore": RestoreClosedTab(); break;
            case "tabNext": CycleTab(1); break;
            case "tabPrev": CycleTab(-1); break;
            case "tabNew": _ = AddTabAsync(null); break;
            case "focusUrl": ShowUrlOverlay(); break;
            case "tabList": ShowTabOverlay(); break;
            case "bookmarkList": ShowBookmarkOverlay(); break;
            case "mpvReturn": MpvReturn(tab); break;
        }
    }

    async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (BookmarkListOverlay.Visibility == Visibility.Visible)
        {
            HandleBookmarkOverlayKey(key, shift, ctrl);
            e.Handled = true;
            return;
        }

        // タブ一覧オーバーレイ表示中はそちらでキーを処理する
        if (TabOverlay.Visibility == Visibility.Visible)
        {
            HandleTabOverlayKey(key, shift, ctrl);
            e.Handled = true;
            return;
        }

        // Shift付きを先に判定しないと Ctrl+T が Ctrl+Shift+T を食う
        if (ctrl && shift && key == Key.W) { e.Handled = true; Close(); }
        else if (ctrl && shift && key == Key.T) { e.Handled = true; RestoreClosedTab(); }
        // Ctrl+Shift+D は Ctrl+D(ブックマーク)より先に判定する
        else if (ctrl && shift && key == Key.D) { e.Handled = true; if (_active is not null) DetachTab(_active); }
        else if (ctrl && shift && key == Key.Y) { e.Handled = true; if (_active is not null) _ = TranslatePageToggleAsync(_active); }
        else if (ctrl && shift && key == Key.PageDown) { e.Handled = true; MoveTab(1); }
        else if (ctrl && shift && key == Key.PageUp) { e.Handled = true; MoveTab(-1); }
        else if (ctrl && key == Key.PageDown) { e.Handled = true; CycleTab(1); }
        else if (ctrl && key == Key.PageUp) { e.Handled = true; CycleTab(-1); }
        else if (ctrl && key == Key.T) { e.Handled = true; await AddTabAsync(null); }
        else if (ctrl && key == Key.W) { e.Handled = true; if (_active != null) CloseTab(_active); }
        else if (ctrl && key == Key.L) { e.Handled = true; ShowUrlOverlay(); }
        else if (ctrl && key == Key.D) { e.Handled = true; Star_Click(this, new RoutedEventArgs()); }
        else if (ctrl && key == Key.E) { e.Handled = true; OpenInEdge(); }
        else if (ctrl && key == Key.M) { e.Handled = true; OpenInMpv(); }
        else if (ctrl && key == Key.B) { e.Handled = true; Focus_Click(this, new RoutedEventArgs()); }
        else if (ctrl && key == Key.O) { e.Handled = true; ToggleVideoFullscreen(); }
        // 素のShift+Tabはページ内フォーカスの逆順移動(ホームページ等の要素選択)に譲るため奪わない。
        // タブ一覧はCtrl+Tab専用にする(旧: Ctrl+Tabでの単純サイクルは廃止しタブ一覧に統合)
        else if (ctrl && key == Key.Tab) { e.Handled = true; ShowTabOverlay(); }
        else if (ctrl && key >= Key.D1 && key <= Key.D8) { e.Handled = true; JumpTab(key - Key.D1); }
        else if (ctrl && key == Key.D9) { e.Handled = true; JumpTab(Tabs.Count - 1); }
        else if (key == Key.F7) { e.Handled = true; ToggleCaretBrowsing(); }
        else if (key == Key.F11) { e.Handled = true; SetFullscreen(!_fullscreen); }
    }

    void RestartApp()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        var urls = Tabs.Select(t => TabUrl(t) ?? "")
                       .Where(u => u.Length > 0 && u != "about:blank");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true };
        if (((App)Application.Current).IsDetachedWindow) psi.ArgumentList.Add(App.DetachedFlag);
        foreach (var url in urls) psi.ArgumentList.Add(url);
        Process.Start(psi);
        Close();
    }

    void SetFullscreen(bool on)
    {
        if (on == _fullscreen) return;
        _fullscreen = on;
        var chrome = WindowChrome.GetWindowChrome(this);
        if (on)
        {
            _preFsState = WindowState;
            TopBar.Visibility = Visibility.Collapsed;
            chrome.CaptionHeight = 0; // 全画面中は動画上部がドラッグ領域にならないように
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            // WindowStyle=None で最大化してもタスクバーを覆うには一度 Normal を経由する
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            TopBar.Visibility = Visibility.Visible;
            chrome.CaptionHeight = 40;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _preFsState;
        }
        UpdateRootMargin();
    }

    // WindowChrome 使用時、最大化するとリサイズ枠ぶん画面外にはみ出すのを補正
    void UpdateRootMargin()
        => RootGrid.Margin = WindowState == WindowState.Maximized && !_fullscreen
            ? new Thickness(8) : new Thickness(0);

    void MinWin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void MaxWin_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void CloseWin_Click(object sender, RoutedEventArgs e) => Close();
}
