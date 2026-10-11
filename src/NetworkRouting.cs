using System.Net;
using System.Net.Http;

namespace Karu;

/// <summary>
/// WebView2 とホスト側 HTTP 通信で共有するネットワーク経路。
/// ブラウザだけをプロキシへ通して翻訳/Twitch の HttpClient が直結すると、接続元IPが漏れるため
/// 両方を必ず同じ設定から組み立てる。
/// </summary>
static class NetworkRouting
{
    static readonly HashSet<string> ProxySchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "socks5",
    };

    /// <summary>単一の HTTP(S)/SOCKS プロキシを検証し、Chromium と HttpClient で使える形にする。</summary>
    public static string? NormalizeProxy(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Any(char.IsWhiteSpace) || value.IndexOfAny(['\"', '\'', '\r', '\n']) >= 0)
            return null;

        // localhost:8080 のような指定は HTTP プロキシとして扱う。
        var candidate = value.Contains("://", StringComparison.Ordinal) ? value : "http://" + value;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            !ProxySchemes.Contains(uri.Scheme) || string.IsNullOrEmpty(uri.Host) ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
            (uri.AbsolutePath.Length > 0 && uri.AbsolutePath != "/"))
            return null;

        // 認証情報はプロセスのコマンドラインへ露出するため受け付けない。
        return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
    }

    public static IEnumerable<string> BrowserArguments(AppSettings settings)
    {
        var proxy = NormalizeProxy(settings.ProxyServer);
        if (proxy is null) yield break;
        yield return "--proxy-server=" + proxy;
        // WebRTC/QUIC のUDPがプロキシ外へ直結して接続元IPを漏らす経路を閉じる。
        yield return "--webrtc-ip-handling-policy=disable_non_proxied_udp";
        yield return "--disable-quic";
    }

    public static bool IsProxyEnabled(AppSettings settings)
        => NormalizeProxy(settings.ProxyServer) is not null;

    /// <summary>翻訳/Twitch用。WebView2 と同じプロキシを通す。</summary>
    public static HttpClient CreateHttpClient(AppSettings settings, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        var proxy = NormalizeProxy(settings.ProxyServer);
        if (proxy is not null)
        {
            handler.Proxy = new WebProxy(proxy) { BypassProxyOnLocal = true };
            handler.UseProxy = true;
        }
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    public static string StatusText(AppSettings settings)
    {
        var proxy = NormalizeProxy(settings.ProxyServer);
        if (proxy is not null)
            return $"検閲回避: プロキシ ON ({new Uri(proxy).Host}) — 変更は再起動後";
        return "検閲回避: プロキシ OFF — settings.json で設定";
    }
}

/// <summary>軽量化とブラウザ防御のトレードオフを一か所で組み立てる。</summary>
static class BrowserOptions
{
    public static string BuildArguments(AppSettings settings)
    {
        var args = new List<string>
        {
            "--autoplay-policy=no-user-gesture-required",
            "--disable-blink-features=AutomationControlled",
            "--process-per-site",
            "--renderer-process-limit=4",
            "--disk-cache-size=134217728",
            "--media-cache-size=52428800",
            "--disable-sync",
            "--disable-domain-reliability",
            "--disable-breakpad",
            "--no-pings",
            "--disable-renderer-accessibility",
            "--disable-notifications",
            "--disable-speech-api",
            "--disable-print-preview",
        };
        var enabled = new List<string> { "QuickIntensiveWakeUpThrottlingAfterLoading", "msWidevinePlatform" };
        var disabled = new List<string>
        {
            "BackForwardCache", "Translate", "OptimizationHints", "Prerender2", "NoStatePrefetch",
            "MediaRouter", "DialMediaRouteProvider", "GlobalMediaControls", "LiveCaption",
            "AutofillServerCommunication", "SegmentationPlatform",
        };

        if (!settings.EnableBrowserProtections)
        {
            // 旧Karu相当。任意サイトを開くブラウザでは危険なので、明示的に選んだ場合だけ有効にする。
            args.Add("--disable-site-isolation-trials");
            args.Add("--disable-background-networking");
            args.Add("--disable-component-extensions-with-background-pages");
            enabled.AddRange(["NetworkServiceInProcess", "NetworkServiceInProcess2"]);
            disabled.AddRange(["msSmartScreenProtection", "AudioServiceOutOfProcess"]);
        }
        if (settings.CaretBrowsing) args.Add("--enable-caret-browsing");

        args.Add("--enable-features=" + string.Join(',', enabled));
        args.Add("--disable-features=" + string.Join(',', disabled));
        args.AddRange(NetworkRouting.BrowserArguments(settings));
        return string.Join(' ', args);
    }
}
