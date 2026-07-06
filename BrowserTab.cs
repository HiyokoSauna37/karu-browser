using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;

namespace Karu;

/// <summary>
/// タブ1つぶんの状態。メモリライフサイクルは3段階:
///   Active(表示中) → Suspended(WebView2のTrySuspend) → Hibernated(WebView破棄・URLと再生位置のみ保持)
/// 休眠中は View が null になり、タブUI(タイトル/favicon)だけが残る。
/// </summary>
public class BrowserTab : INotifyPropertyChanged
{
    /// <summary>実体のWebView。休眠中は null。</summary>
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

    public BrowserTab(WebView2 view) => View = view;

    /// <summary>休眠中かどうか。タブUIの表示(半透明化)にもバインドされる。</summary>
    public bool IsAsleep => View is null;

    /// <summary>WebViewを手放して休眠状態に入る (Dispose は呼び出し側で済ませること)。</summary>
    public void Detach(string? url, double videoPos)
    {
        View = null;
        SleepUrl = url;
        SleepVideoPos = videoPos;
        OnChanged(nameof(IsAsleep));
    }

    /// <summary>新しいWebViewを割り当てて休眠から復帰する。</summary>
    public void Attach(WebView2 view)
    {
        View = view;
        SleepUrl = null;
        SleepVideoPos = 0;
        LastVideoPos = 0;
        OnChanged(nameof(IsAsleep));
    }

    string _title = "新しいタブ";
    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; OnChanged(); } }
    }

    ImageSource? _favicon;
    public ImageSource? Favicon
    {
        get => _favicon;
        set { if (!Equals(_favicon, value)) { _favicon = value; OnChanged(); } }
    }

    bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive != value) { _isActive = value; OnChanged(); } }
    }

    /// <summary>ナビゲーション中かどうか (NavigationStarting〜Completed)。タイトルバーの炎表示に使う。</summary>
    public bool IsLoading;

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
