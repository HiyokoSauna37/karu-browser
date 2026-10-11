using Karu;

var failures = new List<string>();

void Check(bool condition, string name)
{
    if (!condition) failures.Add(name);
}

void Equal(string? expected, string? actual, string name)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
        failures.Add($"{name}: expected <{expected}>, actual <{actual}>");
}

Equal("http://127.0.0.1:8080", NetworkRouting.NormalizeProxy("127.0.0.1:8080"), "HTTP proxy normalization");
Equal("socks5://127.0.0.1:9050", NetworkRouting.NormalizeProxy("socks5://127.0.0.1:9050"), "SOCKS proxy normalization");
Check(NetworkRouting.NormalizeProxy("socks4://127.0.0.1:9050") is null, "SOCKS4 with local DNS rejected");
Check(NetworkRouting.NormalizeProxy("http://user:pass@proxy.example:8080") is null, "proxy credentials rejected");
Check(NetworkRouting.NormalizeProxy("proxy:8080 --disable-web-security") is null, "proxy argument injection rejected");
var proxyArgs = BrowserOptions.BuildArguments(new AppSettings { ProxyServer = "socks5://127.0.0.1:9050" });
Check(proxyArgs.Contains("--proxy-server=socks5://127.0.0.1:9050", StringComparison.Ordinal), "proxy browser switch");
Check(proxyArgs.Contains("--webrtc-ip-handling-policy=disable_non_proxied_udp", StringComparison.Ordinal),
    "WebRTC direct route disabled with proxy");
Check(proxyArgs.Contains("--disable-quic", StringComparison.Ordinal), "QUIC direct route disabled with proxy");
Check(NetworkRouting.IsProxyEnabled(new AppSettings { ProxyServer = "127.0.0.1:8080" }), "proxy enabled detection");
Check(!NetworkRouting.IsProxyEnabled(new AppSettings()), "proxy disabled detection");
var protectedSettings = new AppSettings();
protectedSettings.Normalize();
var protectedArgs = BrowserOptions.BuildArguments(protectedSettings);
Check(!protectedArgs.Contains("--disable-site-isolation-trials", StringComparison.Ordinal), "site isolation protected by default");
Check(!protectedArgs.Contains("msSmartScreenProtection", StringComparison.Ordinal), "SmartScreen protected by default");
Check(!protectedArgs.Contains("--disable-background-networking", StringComparison.Ordinal), "protection networking kept by default");

protectedSettings.EnableBrowserProtections = false;
var unsafeArgs = BrowserOptions.BuildArguments(protectedSettings);
Check(unsafeArgs.Contains("--disable-site-isolation-trials", StringComparison.Ordinal), "explicit low-memory site isolation switch");
Check(unsafeArgs.Contains("msSmartScreenProtection", StringComparison.Ordinal), "explicit low-memory SmartScreen switch");
Check(unsafeArgs.Contains("--disable-background-networking", StringComparison.Ordinal), "explicit low-memory background networking switch");

Equal("http://localhost:3000", UrlUtil.Normalize("localhost:3000"), "localhost URL");
Equal("http://[::1]:8080", UrlUtil.Normalize("[::1]:8080"), "IPv6 URL");
Check(UrlUtil.Normalize("javascript:alert(1)").Contains("google.com/search", StringComparison.Ordinal),
    "javascript scheme searched instead of executed");
Check(UrlUtil.Normalize("data:text/html,test").Contains("google.com/search", StringComparison.Ordinal),
    "data scheme searched instead of executed");

Check(TwitchAds.IsUsher("https://usher.ttvnw.net/api/channel/hls/test.m3u8"), "Twitch usher accepted");
Check(!TwitchAds.IsUsher("https://evil.example/?u=usher.ttvnw.net/api/test"), "lookalike usher rejected");
Check(TwitchAds.IsMediaPlaylist("https://video-weaver.tyo01.playlist.ttvnw.net/v1/playlist/test.m3u8"),
    "Twitch media playlist accepted");
Check(!TwitchAds.IsMediaPlaylist("https://evil.example/v1/playlist/test.m3u8?x=.playlist.ttvnw.net"),
    "lookalike media playlist rejected");

var invalidSettings = new AppSettings
{
    MaxFps = -1,
    WarmTabs = -1,
    HibernateColdSeconds = -1,
    PressureAvailableGB = double.NaN,
};
invalidSettings.Normalize();
Check(invalidSettings.MaxFps == 0, "MaxFps clamped");
Check(invalidSettings.WarmTabs == 0, "WarmTabs clamped");
Check(invalidSettings.HibernateColdSeconds == 15, "hibernate seconds clamped");
Check(invalidSettings.PressureAvailableGB == 1.5, "NaN memory threshold repaired");

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} test(s) failed:");
    foreach (var failure in failures) Console.Error.WriteLine("- " + failure);
    return 1;
}

Console.WriteLine("All Karu core tests passed.");
return 0;
