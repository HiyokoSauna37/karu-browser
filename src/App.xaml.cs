using System.IO;
using System.Windows;

namespace Karu;

public partial class App : Application
{
    public string[] LaunchUrls { get; private set; } = Array.Empty<string>();

    /// <summary>タブ分離(Ctrl+Shift+D)で起動された窓かどうか。終了時のセッション保存を上書きでなく合流にする。</summary>
    public bool IsDetachedWindow { get; private set; }

    /// <summary>分離窓として起動するときの引数。URL より前に置く。</summary>
    public const string DetachedFlag = "--detached";

    protected override void OnStartup(StartupEventArgs e)
    {
        IsDetachedWindow = e.Args.Contains(DetachedFlag, StringComparer.Ordinal);
        LaunchUrls = e.Args.Where(a => a != DetachedFlag).ToArray();
        // 予期しない例外はログに残して続行する (%APPDATA%\Karu\crash.log)。
        // ブラウザとして「例外1つで全タブ道連れ」は最悪の結果なので落とさない。過去の記録(WebView2破棄直後の
        // ObjectDisposedException 等)はどれも続行して問題ない種類だった。念のためこの時点でセッションも書いておき、
        // 続く例外で本当に落ちても直前の状態へ戻れるようにする。
        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            try { (Current.MainWindow as MainWindow)?.SaveSessionNow(); } catch { }
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log(args.Exception);
            args.SetObserved();
        };
        base.OnStartup(e);
    }

    static void Log(Exception ex)
    {
        try
        {
            File.AppendAllText(Path.Combine(Paths.AppDataDir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }
}
