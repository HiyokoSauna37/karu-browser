using System.IO;
using System.Windows;

namespace Karu;

public partial class App : Application
{
    public string[] LaunchUrls { get; private set; } = Array.Empty<string>();

    protected override void OnStartup(StartupEventArgs e)
    {
        LaunchUrls = e.Args;
        // 予期しない例外はログに残す (%APPDATA%\Karu\crash.log)。
        // UI例外後は破損状態で継続せず通常どおり終了する。
        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            // 未知のUI例外後に破損した状態で動き続けず、ログを残して通常どおり終了する。
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
