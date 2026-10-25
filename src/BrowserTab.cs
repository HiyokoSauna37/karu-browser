using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Wpf;

namespace Karu;

/// <summary>
/// タブ1つぶんの状態。メモリライフサイクルは3段階:
///   Active(表示中) → Suspended(WebView2のTrySuspend) → Hibernated(WebView破棄・URLと再生位置のみ保持)
/// 休眠中は View が null になり、タブUI(タイトル)だけが残る。
/// </summary>
public class BrowserTab : INotifyPropertyChanged
{
    /// <summary>実体のWebView。休眠中と Attach 前は null。</summary>
    public WebView2? View { get; private set; }

    /// <summary>このタブが非アクティブになった時刻。サスペンド/休眠判定に使う。</summary>
    public DateTime HiddenAt = DateTime.Now;

    /// <summary>サスペンド直前に記録した動画の再生位置(秒)。休眠→復帰時のシークに使う。</summary>
    public double LastVideoPos;

    /// <summary>休眠中に保持するURL (null なら復帰時はスタートページ)。</summary>
    public string? SleepUrl { get; private set; }

    /// <summary>休眠中に保持する動画再生位置(秒)。</summary>
    public double SleepVideoPos { get; private set; }

    /// <summary>mpv再生中プレースホルダーから戻るためのURLと再生位置。</summary>
    public string? MpvReturnUrl;
    public double MpvReturnPos;

    /// <summary>直近にYouTube広告スキップの本物クリックを打った時刻(連打抑止)。</summary>
    public DateTime LastAdSkipClick = DateTime.MinValue;

    /// <summary>最後に見た実URL(about:blank 以外)。ブラウザプロセス消失後は CoreWebView2.Source が about:blank に
    /// 化けるか取れなくなる(実測)ため、休眠とセッション保存はこれへ落ちる。</summary>
    public string? LastUrl;

    /// <summary>WebViewを手放して休眠状態に入る (Dispose は呼び出し側で済ませること)。</summary>
    public void Detach(string? url, double videoPos)
    {
        View = null;
        SleepUrl = url;
        SleepVideoPos = videoPos;
        Interlocked.Increment(ref _viewGeneration);
        Interlocked.Exchange(ref _activeDownloads, 0);
    }

    /// <summary>新しいWebViewを割り当てる(新規タブの初回装着と休眠からの復帰)。</summary>
    public void Attach(WebView2 view)
    {
        View = view;
        SleepUrl = null;
        SleepVideoPos = 0;
        LastVideoPos = 0;
        Interlocked.Increment(ref _viewGeneration);
        Interlocked.Exchange(ref _activeDownloads, 0);
    }

    string _title = "新しいタブ";
    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; OnChanged(); } }
    }

    /// <summary>ナビゲーション中かどうか (NavigationStarting〜Completed)。タイトルバーの炎表示に使う。</summary>
    public bool IsLoading;

    int _activeDownloads;
    long _viewGeneration;
    /// <summary>進行中ダウンロードの間はWebViewの自動休眠を禁止する。</summary>
    internal bool HasActiveDownloads => Volatile.Read(ref _activeDownloads) > 0;
    internal long BeginDownload()
    {
        long generation = Volatile.Read(ref _viewGeneration);
        Interlocked.Increment(ref _activeDownloads);
        return generation;
    }
    internal void EndDownload(long generation)
    {
        while (true)
        {
            // 破棄済みの旧WebViewから遅れて届いた完了通知で、
            // 復帰後の新しいWebViewのダウンロード数を減らさない。
            if (Volatile.Read(ref _viewGeneration) != generation) return;
            int current = Volatile.Read(ref _activeDownloads);
            if (current == 0) return;
            if (Interlocked.CompareExchange(ref _activeDownloads, current - 1, current) == current) return;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
