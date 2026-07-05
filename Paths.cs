using System.IO;

namespace Karu;

static class Paths
{
    /// <summary>設定・お気に入り・ブロックリスト置き場 (%APPDATA%\Karu)</summary>
    public static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Karu");

    /// <summary>Cookie・ログイン情報などWebView2のプロファイル (%LOCALAPPDATA%\Karu)</summary>
    public static readonly string WebDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Karu", "WebView2Data");

    /// <summary>ここに展開した拡張機能(uBlock Origin等)を起動時に自動ロードする</summary>
    public static readonly string ExtensionsDir = Path.Combine(AppDataDir, "extensions");

    static Paths()
    {
        Directory.CreateDirectory(AppDataDir);
        Directory.CreateDirectory(WebDataDir);
        Directory.CreateDirectory(ExtensionsDir);
    }
}
