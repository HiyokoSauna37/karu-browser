using System.Diagnostics;
using System.IO;

namespace Karu;

/// <summary>mpv (+yt-dlp) の起動。winget ユーザースコープ導入で PATH 未反映でも見つけられるようにする。</summary>
static class Mpv
{
    static string? FindWinGetExe(string name)
    {
        var pkgRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\WinGet\Packages");
        if (!Directory.Exists(pkgRoot)) return null;
        foreach (var dir in Directory.GetDirectories(pkgRoot))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>DRM配信サイト (Karu本体では再生できるが、mpv+yt-dlp へのオフロードだけは不可能)</summary>
    static readonly string[] DrmHosts =
    {
        "netflix.com", "primevideo.com", "amazon.co.jp", "amazon.com",
        "disneyplus.com", "abema.tv", "hulu.jp", "unext.jp", "video.unext.jp",
        "dazn.com", "lemino.docomo.ne.jp", "wowow.co.jp", "spotify.com",
    };

    /// <summary>mpvで再生できないDRMサイトかどうか。</summary>
    public static bool IsDrmSite(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var host = u.Host;
        return DrmHosts.Any(d => host == d || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>mpv を起動し、そのプロセスを返す (見つからなければ null)。
    /// ライブ配信は --start を付けず、キャッシュを厚めに取る。</summary>
    public static Process? TryLaunch(string url, double startSeconds, bool isLive)
    {
        var links = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Links");
        var ytdlp = new[] { Path.Combine(links, "yt-dlp.exe"), FindWinGetExe("yt-dlp.exe") }
            .FirstOrDefault(p => p is not null && File.Exists(p));
        var args = ytdlp is not null ? $"--script-opts=ytdl_hook-ytdl_path=\"{ytdlp}\" " : "";
        if (isLive)
            args += "--demuxer-max-bytes=256MiB --cache=yes ";
        else if (startSeconds > 5)
            args += $"--start={(int)startSeconds} ";
        args += $"--force-window=immediate \"{url}\"";

        foreach (var exe in new[] { "mpv.exe", Path.Combine(links, "mpv.exe"), FindWinGetExe("mpv.exe"), "mpvnet.exe" })
        {
            if (exe is null) continue;
            try
            {
                return Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
            }
            catch { }
        }
        return null;
    }
}
