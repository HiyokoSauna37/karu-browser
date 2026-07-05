using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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
    readonly DispatcherTimer _sleepTimer;
    readonly List<string> _closedUrls = new();
    readonly bool _useBuiltinBlock; // 拡張(uBlock)が無いときだけ簡易ブロッカーを使う
    Task<CoreWebView2Environment>? _envTask;
    BrowserTab? _active;
    bool _extensionsLoaded;
    bool _fullscreen;
    WindowState _preFsState = WindowState.Normal;
    DateTime _minimizedAt = DateTime.Now;

    static readonly Brush StarOffBrush = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));
    static readonly Brush StarOnBrush = new SolidColorBrush(Color.FromRgb(0xD4, 0x84, 0x5E));

    public MainWindow()
    {
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
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _bookmarks.Load();
        _adblock.Load();
        var urls = ((App)Application.Current).LaunchUrls;
        if (urls.Length == 0) urls = SessionStore.Load();
        try
        {
            if (urls.Length == 0) await AddTabAsync(null);
            else foreach (var u in urls) await AddTabAsync(u);
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
        SessionStore.Save(Tabs
            .Select(t => TabUrl(t) ?? "")
            .Where(u => u.Length > 0 && u != "about:blank"));
        foreach (var t in Tabs) t.View?.Dispose();
    }

    /// <summary>タブの現在URL。休眠中は保持しているURLを返す。</summary>
    static string? TabUrl(BrowserTab t)
    {
        if (t.View is null) return t.SleepUrl;
        try { return t.View.CoreWebView2?.Source; } catch { return t.SleepUrl; }
    }

    Task<CoreWebView2Environment> GetEnvAsync()
        => _envTask ??= CoreWebView2Environment.CreateAsync(null, Paths.WebDataDir,
            new CoreWebView2EnvironmentOptions
            {
                // 軽量化フラグ:
                //   process-per-site + renderer-process-limit=4: プロセス数を抑える。
                //     ※2まで絞るとWarmTabs+アクティブでプロセス共有が常態化し、遷移先ページが
                //       稼働中サイト(YouTube等)のレンダラー主スレッドを奪い合って遷移が重くなる
                //   disable-site-isolation-trials: クロスサイトiframe用の別レンダラーを作らない
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
                //   msSmartScreenProtection無効: SmartScreen常駐プロセスを止める(フィッシング警告も消える)
                //   ※ --enable-low-end-device-mode と --js-flags=--optimize-for-size は、表示中タブの
                //     V8実行やキャッシュまで遅くしてURL遷移の体感が悪化するため使わない
                //     (裏タブのメモリはサスペンド/休眠側で回収済みで、これらの追加削減効果は小さい)
                AdditionalBrowserArguments =
                    "--autoplay-policy=no-user-gesture-required " +
                    "--process-per-site --renderer-process-limit=4 " +
                    "--disable-site-isolation-trials " +
                    "--disk-cache-size=134217728 --media-cache-size=52428800 --disable-sync " +
                    "--disable-background-networking --disable-component-update " +
                    "--disable-domain-reliability --disable-breakpad --no-pings " +
                    "--disable-renderer-accessibility --disable-notifications " +
                    "--disable-speech-api --disable-print-preview " +
                    "--disable-component-extensions-with-background-pages " +
                    "--enable-features=QuickIntensiveWakeUpThrottlingAfterLoading," +
                    "NetworkServiceInProcess,NetworkServiceInProcess2 " +
                    "--disable-features=BackForwardCache,Translate,OptimizationHints,msSmartScreenProtection," +
                    "Prerender2,NoStatePrefetch,MediaRouter,DialMediaRouteProvider," +
                    "GlobalMediaControls,LiveCaption,AutofillServerCommunication,SegmentationPlatform," +
                    "AudioServiceOutOfProcess" +
                    (_settings.CaretBrowsing ? " --enable-caret-browsing" : ""),
                // 拡張(uBlock等)が置かれていないときは拡張基盤ごと起動しない
                AreBrowserExtensionsEnabled = !_useBuiltinBlock,
                // 内蔵トラッキング防止エンジンを止めてメモリ削減 (トラッカー遮断は blocklist.txt 側で実施)
                EnableTrackingPrevention = false,
            });

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

    static WebView2 NewView() => new()
    {
        DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 0x11, 0x11, 0x11),
    };

    async Task<BrowserTab> AddTabAsync(string? url)
    {
        var view = NewView();
        var tab = new BrowserTab(view);
        Tabs.Add(tab);
        WebHost.Children.Add(view);
        ActivateTab(tab);

        var core = await InitCoreAsync(tab, view);
        if (core is null) return tab;

        if (url is null) ShowStartPage(tab);
        else Navigate(tab, url);
        return tab;
    }

    /// <summary>WebView2の初期化と全イベント配線。新規タブと休眠復帰の両方から使う。
    /// 環境生成の失敗はここから throw され、OnLoaded 側で表示される。</summary>
    async Task<CoreWebView2?> InitCoreAsync(BrowserTab tab, WebView2 view)
    {
        var env = await GetEnvAsync();
        try { await view.EnsureCoreWebView2Async(env); }
        catch { return null; } // 初期化完了前にタブが閉じられた場合

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
        scripts.Add(Injections.YouTube(_settings.FocusMode, _settings.MaxQuality));
        scripts.Add(Injections.Vim);
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
        core.NavigationCompleted += (_, _) =>
        {
            // キーボード主体で使うため、読み込み完了時にページへフォーカスを渡す
            if (tab == _active && !IsOverlayOpen) tab.View?.Focus();
        };
        core.WebMessageReceived += (_, args) =>
        {
            string? cmd = null;
            try { cmd = args.TryGetWebMessageAsString(); } catch { }
            if (cmd is not null && cmd.StartsWith("bookmarkGoNew:"))
            {
                _ = AddTabAsync(cmd[14..]); return;
            }
            if (cmd is not null && cmd.StartsWith("bookmarkGo:"))
            {
                Navigate(tab, cmd[11..]); return;
            }
            switch (cmd)
            {
                case "tabClose": CloseTab(tab); break;
                case "tabRestore": RestoreClosedTab(); break;
                case "tabNext": CycleTab(1); break;
                case "tabPrev": CycleTab(-1); break;
                case "tabNew": _ = AddTabAsync(null); break;
                case "focusUrl": ShowUrlOverlay(); break;
                case "tabList": ShowTabOverlay(); break;
                case "bookmarkList": ShowBookmarkOverlay(tab); break;
                case "mpvReturn": MpvReturn(tab); break;
            }
        };
        core.ContainsFullScreenElementChanged += (_, _) => SetFullscreen(core.ContainsFullScreenElement);

        // 「表示しないデータを受信しない」レイヤー:
        //   - Sec-Purpose/Purpose: prefetch が付いた投機的リクエストを遮断 (Prerender2無効化の取りこぼし対策)
        //   - Save-Data: on を送り、対応サイトには軽量版レスポンスを要求
        //   - 拡張が無ければ blocklist.txt による広告・トラッカー遮断もここで行う
        // フィルタ対象のリクエストは1件ずつネットワーク処理が止まってUIスレッドへCOM往復するため、
        // 対象種別を増やすほどページ読み込み全体が遅くなる。uBlock等の拡張が遮断を担う構成では
        // Document(ナビゲーション本体+iframe)だけに絞り、内蔵ブロッカー構成のときだけ
        // 広告・先読みが通るサブリソース種別も対象にする (Media/フォントは常に素通し)
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
            try { req.Headers.SetHeader("Save-Data", "on"); } catch { }
        };
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Document);
        if (_useBuiltinBlock)
            foreach (var ctx in new[]
            {
                CoreWebView2WebResourceContext.Script,
                CoreWebView2WebResourceContext.Image,
                CoreWebView2WebResourceContext.XmlHttpRequest,
                CoreWebView2WebResourceContext.Fetch,
                CoreWebView2WebResourceContext.Ping,
            })
                core.AddWebResourceRequestedFilter("*", ctx);

        return core;
    }

    async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

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
        var args = string.Join(" ", urls.Select(u => $"\"{u}\""));
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
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
