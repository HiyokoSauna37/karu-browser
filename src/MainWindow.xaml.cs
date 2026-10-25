using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Karu;

/// <summary>本体の構築・起動・終了とセッション管理。
/// WebView2の生成と通信は MainWindow.WebView.cs、入力とウィンドウ操作は MainWindow.Commands.cs、
/// タブ切替/休眠は MainWindow.Tabs.cs、オーバーレイは MainWindow.Overlays.cs、
/// 機能系(mpv/翻訳/集中モード/メニュー)は MainWindow.Features.cs。</summary>
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

    // ナビゲーション中の炎アニメーション (2フレームを交互表示。クリック直後に「もう始まっている」を伝える)
    // 表示は16 DIPのため、256px原画の展開は64pxまでにして400% DPIまで原寸を保つ。
    static readonly BitmapImage FlameFrame1 = DecodeResourceBitmap("pack://application:,,,/karu-flame1.png", 64);
    static readonly BitmapImage FlameFrame2 = DecodeResourceBitmap("pack://application:,,,/karu-flame2.png", 64);
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
        var urls = ContentUrls();
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

    /// <summary>保存・復元・分離の対象になる実URL。空タブ(about:blank)や取得不能なら null。</summary>
    static string? ContentUrl(BrowserTab t)
    {
        var url = TabUrl(t);
        return string.IsNullOrEmpty(url) || url == "about:blank" ? null : url;
    }

    /// <summary>全タブの実URL(タブ順)。セッション保存と再起動の引き継ぎに使う。</summary>
    IEnumerable<string> ContentUrls() => Tabs.Select(ContentUrl).OfType<string>();

    /// <summary>ストリームから即時デコードして凍結したオーバーレイ背景を作る。</summary>
    static BitmapImage DecodeBitmap(Stream stream, int decodePixelWidth = 0)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        if (decodePixelWidth > 0) img.DecodePixelWidth = decodePixelWidth;
        img.StreamSource = stream;
        img.EndInit();
        img.Freeze();
        return img;
    }

    /// <summary>パッケージ画像を表示サイズに合わせてデコードし、共有可能な不変画像にする。</summary>
    static BitmapImage DecodeResourceBitmap(string uri, int decodePixelWidth)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.DecodePixelWidth = decodePixelWidth;
        img.UriSource = new Uri(uri, UriKind.Absolute);
        img.EndInit();
        img.Freeze();
        return img;
    }

    /// <summary>結果を待たないスクリプト実行。破棄済み/消失後の WebView では例外になるが、その Task を観測しないと
    /// finalizer スレッドの UnobservedTaskException として crash.log に残るだけなので、ここで受け止めて捨てる。</summary>
    static async void FireScript(CoreWebView2 core, string js)
    {
        try { await core.ExecuteScriptAsync(js); } catch { }
    }

}
