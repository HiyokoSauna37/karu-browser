using System.IO;

namespace Karu;

static class Paths
{
    /// <summary>設定・お気に入り・ブロックリスト置き場 (%APPDATA%\Karu)。
    /// 環境変数 KARU_PROFILE_DIR があればそのフォルダを使う(別プロファイルで検証用インスタンスを起動するため)。</summary>
    public static readonly string AppDataDir = ProfileOverride() ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Karu");

    /// <summary>Cookie・ログイン情報などWebView2のプロファイル (%LOCALAPPDATA%\Karu)。KARU_PROFILE_DIR 指定時はその配下。</summary>
    public static readonly string WebDataDir = ProfileOverride() is { } profile
        ? Path.Combine(profile, "WebView2Data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Karu", "WebView2Data");

    static string? ProfileOverride()
    {
        var dir = Environment.GetEnvironmentVariable("KARU_PROFILE_DIR");
        return string.IsNullOrWhiteSpace(dir) ? null : Path.GetFullPath(dir);
    }

    /// <summary>ここに展開した拡張機能(uBlock Origin等)を起動時に自動ロードする</summary>
    public static readonly string ExtensionsDir = Path.Combine(AppDataDir, "extensions");

    static Paths()
    {
        Directory.CreateDirectory(AppDataDir);
        Directory.CreateDirectory(WebDataDir);
        Directory.CreateDirectory(ExtensionsDir);
    }

    /// <summary>一時ファイルへ書いてから置換する。書き込み途中で落ちても既存ファイルを壊さない。</summary>
    public static void AtomicWrite(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}
