using System.IO;
using System.Windows;

namespace Karu;

public partial class App : Application
{
    public string[] LaunchUrls { get; private set; } = Array.Empty<string>();

    protected override void OnStartup(StartupEventArgs e)
    {
        LaunchUrls = e.Args;
        // 予期しない例外は落とさずログに残す (%APPDATA%\Karu\crash.log)
        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
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
