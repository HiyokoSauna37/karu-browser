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

Check(YouTubeAdSkip.TryParseCommand("adSkip|640|360", out var adX, out var adY) && adX == 640 && adY == 360,
    "ad skip command parsed");
Check(!YouTubeAdSkip.TryParseCommand("adSkip|-1|360", out _, out _), "negative ad skip coordinate rejected");
Check(!YouTubeAdSkip.TryParseCommand("adSkip|12.5|360", out _, out _), "fractional ad skip coordinate rejected");
Check(!YouTubeAdSkip.TryParseCommand("adSkip|99999|360", out _, out _), "out-of-range ad skip coordinate rejected");
Check(!YouTubeAdSkip.TryParseCommand("adSkip|1|2|3", out _, out _), "malformed ad skip command rejected");
Check(!YouTubeAdSkip.TryParseCommand("tabClose", out _, out _), "non ad skip command rejected");
Check(YouTubeAdSkip.IsYouTubeHost("www.youtube.com") && YouTubeAdSkip.IsYouTubeHost("youtube.com"), "YouTube host accepted");
Check(!YouTubeAdSkip.IsYouTubeHost("youtube.com.evil.example") && !YouTubeAdSkip.IsYouTubeHost("notyoutube.com"),
    "lookalike YouTube host rejected");
var clickSeq = YouTubeAdSkip.ClickSequence(10, 20).ToList();
Check(clickSeq.Count == 2 && clickSeq[0].Contains("mousePressed") && clickSeq[1].Contains("mouseReleased"),
    "trusted click is press then release (no mouseMoved: it never completes on a hidden WebView)");
Check(clickSeq.All(j => j.Contains("\"button\":\"left\"") && j.Contains("\"clickCount\":1")
    && j.Contains("\"x\":10") && j.Contains("\"y\":20")), "trusted click is a left single click at the point");
var ytScript = Injections.YouTube(false, "", true, true);
Check(ytScript.Contains(Injections.MessageToken, StringComparison.Ordinal), "YouTube script carries the message token");
Check(Injections.MessageToken.Length == 32 && Injections.Vim.Contains(Injections.MessageToken, StringComparison.Ordinal),
    "Vim script carries the initialized message token across partial class files");
Check(!Injections.Vim.Contains("__KARU_MSG_TOKEN__", StringComparison.Ordinal), "Vim message token placeholder replaced");
Check(ytScript.Contains("adSkip|", StringComparison.Ordinal), "YouTube script asks the host for the skip click");
Check(!ytScript.Contains(".click()", StringComparison.Ordinal), "YouTube script never clicks ad controls synthetically");
Check(!ytScript.Contains("currentTime =", StringComparison.Ordinal),
    "YouTube script never seeks (any seek inside an ad is reported, and an early one stalls the ad for 10 s)");
Check(ytScript.Contains("const AD_RATE = 2.4, AD_LEAD = 0.3;", StringComparison.Ordinal),
    "ad speed-up stays under the player's 2.5x media-time vs wall-clock check, after a 1x lead-in");
Check(ytScript.Contains("adPollId = setInterval(adTick, 100)", StringComparison.Ordinal) &&
      ytScript.Contains("clearInterval(adPollId)", StringComparison.Ordinal),
    "YouTube 100 ms polling only runs while an ad is active");
Check(!ytScript.Contains("setInterval(() => { if (ad.on) adTick(); }, 100)", StringComparison.Ordinal),
    "YouTube has no permanent 100 ms ad timer");
Check(ytScript.Contains("if (!hid) {", StringComparison.Ordinal),
    "YouTube focus-mode selector sweep runs only when entering focus mode");
Check(Injections.PageCover.Contains("if (self !== top) return;", StringComparison.Ordinal),
    "page cover only runs in the top-level frame");
Check(Injections.PageCover.Contains("busyObserver?.disconnect()", StringComparison.Ordinal),
    "page cover disconnects long-task observation after loading");
Check(!Injections.Vim.Contains("mouse = [e.clientX, e.clientY]", StringComparison.Ordinal),
    "mousemove tracking does not allocate a coordinate array per event");
Check(Injections.TranslateApplyFn.Contains("st.units = null", StringComparison.Ordinal),
    "completed translation releases temporary DOM assembly references");
var memo = new TranslationMemo(entryCap: 2, characterCap: 12);
memo.Set("a", "11", null);
memo.Set("b", "22", null);
_ = memo.Get("a"); // a を最近使用にし、次の追加で b が追い出されることを確認
memo.Set("c", "33", null);
Check(memo.Get("a") is not null && memo.Get("b") is null && memo.Get("c") is not null && memo.Count == 2,
    "translation memo evicts the least recently used entry at its count limit");
memo.Set("long", "0123456789", null);
Check(memo.Get("long") is null && memo.Characters <= 12,
    "translation memo skips a single entry larger than its character budget");
var charMemo = new TranslationMemo(entryCap: 10, characterCap: 8);
charMemo.Set("aa", "11", null);
charMemo.Set("bb", "222", null);
Check(charMemo.Get("aa") is null && charMemo.Get("bb") is not null && charMemo.Characters == 5,
    "translation memo evicts LRU entries until it fits its character budget");
// YouTube などは Trusted Types を強制していて、HTML文字列を DOM へ入れる API は例外になる(実測)
foreach (var (name, js) in new[]
{
    ("PageCover", Injections.PageCover), ("LowSpec", Injections.LowSpec), ("MediaPolicy", Injections.MediaPolicy(true, 30)),
    ("TranslateState", Injections.TranslateState), ("TranslateCollect", Injections.TranslateCollect),
    ("TranslateApplyFn", Injections.TranslateApplyFn), ("TranslateReapply", Injections.TranslateReapply),
    ("TranslateRestore", Injections.TranslateRestore), ("TwitchQuality", Injections.TwitchQuality),
    ("TwitchAdCover", Injections.TwitchAdCover), ("YouTube", ytScript), ("Vim", Injections.Vim),
})
    Check(!new[] { ".innerHTML", ".outerHTML", "insertAdjacentHTML", "document.write", "parseFromString", "createContextualFragment" }
        .Any(sink => js.Contains(sink, StringComparison.Ordinal)),
        name + " uses no HTML-string sink (Trusted Types sites such as YouTube throw on them)");
Check(Injections.YouTube(false, "", false, true).Contains("const AD_SPEED_UP = false", StringComparison.Ordinal), "ad speed-up can be disabled");
Check(Injections.YouTube(false, "", true, false).Contains("const AD_COVER = false", StringComparison.Ordinal), "ad cover can be disabled");
var blocker = new AdBlocker();
blocker.LoadLines(["# comment", "", "doubleclick.net", " googlesyndication.com "]);
Check(blocker.Count == 2, "blocklist lines: comments and blanks skipped, entries trimmed");
Check(!blocker.ShouldBlock("https://static.doubleclick.net/instream/ad_status.js"),
    "YouTube's ad_status.js bait passes (blocking it only reports Karu as an ad blocker)");
Check(!blocker.ShouldBlock("https://googleads.g.doubleclick.net/pagead/id?slf_rd=1"), "YouTube's pagead/id bait passes");
Check(blocker.ShouldBlock("https://googleads.g.doubleclick.net/pagead/viewthroughconversion/1/?x=1"),
    "other doubleclick paths on the bait host stay blocked");
Check(blocker.ShouldBlock("https://static.doubleclick.net/instream/ad_status.js.evil"), "bait exemption is an exact path");
Check(blocker.ShouldBlock("https://ad.doubleclick.net/instream/ad_status.js"), "bait exemption is tied to its host");
Check(blocker.ShouldBlock("https://tpc.googlesyndication.com/sodar/x.js"), "subdomain of a listed domain is blocked");
Check(!blocker.ShouldBlock("https://www.youtube.com/watch?v=x"), "unlisted host passes");
var adDefaults = new AppSettings();
adDefaults.Normalize();
Check(adDefaults.YouTubeAdSpeedUp, "ad speed-up on by default");
Check(adDefaults.YouTubeAdCover, "ad cover on by default");
Check(adDefaults.MaxLiveTabs == 6, "six live tabs kept by default");

Equal("mailto:a@b.example", UrlUtil.Normalize("mailto:a@b.example"), "mailto passes through to the mail client");
Check(UrlUtil.Normalize("ftp://x.example/z").Contains("google.com/search", StringComparison.Ordinal),
    "explicit unsupported scheme is searched, not prefixed with https://");
Check(UrlUtil.Normalize("chrome://settings").Contains("google.com/search", StringComparison.Ordinal), "internal scheme is searched");
Equal("https://example.com:8080/x", UrlUtil.Normalize("example.com:8080/x"), "host:port with path still gets https");

var badProxy = new AppSettings { ProxyServer = " http://user:pw@proxy.example:8080 " };
badProxy.Normalize();
Equal("http://user:pw@proxy.example:8080", badProxy.ProxyServer, "invalid proxy is kept (trimmed), not silently erased");
Check(!NetworkRouting.IsProxyEnabled(badProxy) && NetworkRouting.HasInvalidProxy(badProxy), "invalid proxy is reported and not used");
Check(NetworkRouting.StatusText(badProxy).Contains("不正", StringComparison.Ordinal), "menu shows the invalid proxy state");
Check(!NetworkRouting.HasInvalidProxy(new AppSettings()), "empty proxy is not an error");

// Twitch master playlists name the rendition differently per usher version (VIDEO= / STABLE-VARIANT-ID=)
const string master = "#EXTM3U\n" +
    "#EXT-X-STREAM-INF:BANDWIDTH=1,VIDEO=\"chunked\"\nhttps://a/chunked.m3u8\n" +
    "#EXT-X-STREAM-INF:BANDWIDTH=2,STABLE-VARIANT-ID=\"720p60\"\nhttps://a/720.m3u8\n" +
    "#EXT-X-STREAM-INF:BANDWIDTH=3\nhttps://a/noname.m3u8\n";
var variants = TwitchAds.Variants(master).ToList();
Check(variants.Count == 3, "three variants parsed");
Equal("chunked", variants[0].Name, "v1 VIDEO attribute read as the name");
Equal("720p60", variants[1].Name, "v2 STABLE-VARIANT-ID read as the name");
Equal("", variants[2].Name, "variant without a name attribute");
Equal("https://a/720.m3u8", TwitchAds.Pick(master, new TwitchAds.Variant("u", "720p60", 0)), "pick by name beats position");
Equal("https://a/noname.m3u8", TwitchAds.Pick(master, new TwitchAds.Variant("u", "1080p60", 2)), "unknown name falls back to position");
Equal("https://a/chunked.m3u8", TwitchAds.Pick(master, new TwitchAds.Variant("u", "", 9)), "position past the end falls back to first");
Check(TwitchAds.Pick("#EXTM3U\n", new TwitchAds.Variant("u", "", 0)) is null, "master without renditions yields nothing");

// Keyed list overlays: which row a key press selects
Check(KeyedList.KeyToChar(System.Windows.Input.Key.D3) == '3' && KeyedList.KeyToChar(System.Windows.Input.Key.NumPad3) == '3',
    "digit and numpad keys map to the same character");
Check(KeyedList.KeyToChar(System.Windows.Input.Key.Q) == 'q', "letter keys map to lowercase");
Check(KeyedList.KeyToChar(System.Windows.Input.Key.Enter) is null && KeyedList.KeyToChar(System.Windows.Input.Key.F1) is null,
    "non-character keys map to nothing");

var invalidSettings = new AppSettings
{
    MaxFps = -1,
    MaxLiveTabs = -1,
    WarmTabs = -1,
    HibernateColdSeconds = -1,
    PressureAvailableGB = double.NaN,
};
invalidSettings.Normalize();
Check(invalidSettings.MaxFps == 0, "MaxFps clamped");
Check(invalidSettings.MaxLiveTabs == 0, "negative live-tab limit means unlimited");
Check(invalidSettings.WarmTabs == 0, "WarmTabs clamped");
Check(invalidSettings.HibernateColdSeconds == 15, "hibernate seconds clamped");
Check(invalidSettings.PressureAvailableGB == 1.5, "NaN memory threshold repaired");
var excessiveSettings = new AppSettings { MaxLiveTabs = 101 };
excessiveSettings.Normalize();
Check(excessiveSettings.MaxLiveTabs == 100, "live-tab limit clamped at its upper bound");

var lifecycle = new AppSettings
{
    WarmTabs = 2,
    HibernateWarmMinutes = 10,
    HibernateColdSeconds = 180,
    ParkMinutes = 10,
};
lifecycle.Normalize();
Check(TabLifecyclePolicy.ForInactiveTab(TimeSpan.FromSeconds(14), 2, false, lifecycle) ==
      TabLifecyclePolicy.Action.None, "fresh background tab stays idle");
Check(TabLifecyclePolicy.ForInactiveTab(TimeSpan.FromSeconds(15), 2, false, lifecycle) ==
      TabLifecyclePolicy.Action.Suspend, "background tab suspends at 15 seconds");
Check(TabLifecyclePolicy.ForInactiveTab(TimeSpan.FromSeconds(180), 2, false, lifecycle) ==
      TabLifecyclePolicy.Action.Hibernate, "cold tab hibernates at configured threshold");
Check(TabLifecyclePolicy.ForInactiveTab(TimeSpan.FromSeconds(180), 1, false, lifecycle) ==
      TabLifecyclePolicy.Action.Suspend, "warm tab keeps its longer hibernation threshold");
Check(TabLifecyclePolicy.ForInactiveTab(TimeSpan.FromMinutes(10), 1, false, lifecycle) ==
      TabLifecyclePolicy.Action.Hibernate, "warm tab hibernates at configured threshold");
Check(TabLifecyclePolicy.ForInactiveTab(TimeSpan.FromSeconds(29), 0, true, lifecycle) ==
      TabLifecyclePolicy.Action.Suspend, "memory pressure does not hibernate before 30 seconds");
Check(TabLifecyclePolicy.ForInactiveTab(TimeSpan.FromSeconds(30), 0, true, lifecycle) ==
      TabLifecyclePolicy.Action.Hibernate, "memory pressure overrides warm-tab retention");
Check(!TabLifecyclePolicy.ShouldHibernateForCapacity(TimeSpan.FromSeconds(59), true),
    "live-tab capacity keeps a 60-second grace period");
Check(!TabLifecyclePolicy.ShouldHibernateForCapacity(TimeSpan.FromSeconds(60), false),
    "live-tab capacity never destroys a tab that WebView2 could not suspend");
Check(TabLifecyclePolicy.ShouldHibernateForCapacity(TimeSpan.FromSeconds(60), true),
    "live-tab capacity hibernates an already suspended tab after its grace period");
var cappedTabs = new AppSettings { MaxLiveTabs = 6 };
Check(!TabLifecyclePolicy.IsOverLiveLimit(5, cappedTabs) &&
      TabLifecyclePolicy.IsOverLiveLimit(6, cappedTabs),
    "live-tab limit includes the active tab and starts beyond six retained tabs");
cappedTabs.MaxLiveTabs = 0;
Check(!TabLifecyclePolicy.IsOverLiveLimit(100, cappedTabs), "zero live-tab limit means unlimited");
var downloadTab = new BrowserTab();
long download1 = downloadTab.BeginDownload();
long download2 = downloadTab.BeginDownload();
downloadTab.EndDownload(download1);
Check(downloadTab.HasActiveDownloads, "tab remains protected until every download finishes");
downloadTab.EndDownload(download2);
downloadTab.EndDownload(download2);
Check(!downloadTab.HasActiveDownloads, "download protection clears and never underflows");
long staleDownload = downloadTab.BeginDownload();
downloadTab.Detach(null, 0);
long currentDownload = downloadTab.BeginDownload();
downloadTab.EndDownload(staleDownload);
Check(downloadTab.HasActiveDownloads, "completion from a disposed WebView cannot clear a new download");
downloadTab.EndDownload(currentDownload);
Check(!TabLifecyclePolicy.ShouldSuspendActiveTab(TimeSpan.FromSeconds(15) - TimeSpan.FromTicks(1)) &&
      TabLifecyclePolicy.ShouldSuspendActiveTab(TimeSpan.FromSeconds(15)),
    "minimized active tab suspends at 15 seconds");
Check(!TabLifecyclePolicy.ShouldPark(TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1), lifecycle) &&
      TabLifecyclePolicy.ShouldPark(TimeSpan.FromMinutes(10), lifecycle),
    "window parks at configured threshold");

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} test(s) failed:");
    foreach (var failure in failures) Console.Error.WriteLine("- " + failure);
    return 1;
}

Console.WriteLine("All Karu core tests passed.");
return 0;
