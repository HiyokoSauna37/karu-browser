using System.IO;

namespace Karu;

/// <summary>
/// ドメイン単位の簡易広告・トラッカーブロッカー。
/// %APPDATA%\Karu\blocklist.txt（1行1ドメイン、# はコメント）を読み込み、
/// リクエスト先ホストがリスト内ドメインまたはそのサブドメインなら遮断する。
/// ただし <see cref="DetectionProbes"/> の URL はドメインが一致しても通す。
/// </summary>
public class AdBlocker
{
    static readonly string FilePath = Path.Combine(Paths.AppDataDir, "blocklist.txt");

    /// <summary>
    /// 広告ブロッカー検出用のおとり(ホスト, パス)。YouTube はこれらが取れるかで広告ブロッカーの有無を判定し、
    /// 結果を再生のたびにサーバーへ送る(ad_status.js → DCLKSTAT、pagead/id → 検出項目 "b.f_")。
    /// どちらも広告の中身ではなく、止めても広告は1本も減らずに「広告ブロッカー使用」と記録されるだけなので、
    /// doubleclick.net を遮断していても通す(2026-10-12、Karu で DCLKSTAT=3 と "b.f_" 検出を実測)。
    /// </summary>
    static readonly (string Host, string Path)[] DetectionProbes =
    [
        ("static.doubleclick.net", "/instream/ad_status.js"),
        ("googleads.g.doubleclick.net", "/pagead/id"),
    ];

    readonly HashSet<string> _domains = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _domains.Count;

    public void Load()
    {
        if (!File.Exists(FilePath))
        {
            try { File.WriteAllText(FilePath, DefaultList); } catch { }
        }
        string[] lines = [];
        try { lines = File.ReadAllLines(FilePath); } catch { }
        LoadLines(lines);
    }

    /// <summary>blocklist.txt と同じ書式の行を読み込む(空行と # で始まる行は無視)。</summary>
    internal void LoadLines(IEnumerable<string> lines)
    {
        _domains.Clear();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            _domains.Add(line);
        }
    }

    /// <summary>
    /// WebResourceRequested に登録するワイルドカードURL（1ドメイン1本）。
    /// "*" で全サブリソースを拾うと1リクエストごとにUIスレッドへCOM往復が発生して読み込みが遅くなるため、
    /// 「ブロック対象になりうるURLだけ」をブラウザ側のglob照合で先に絞る。
    /// globはURI全体に対する素朴な照合なので `https://example.com/?u=doubleclick.net/` のような
    /// 無関係なURLも拾いうるが、最終判定は <see cref="ShouldBlock"/> のホスト照合が行うので誤遮断にはならない
    /// (余計に拾った分はCOM往復1回の無駄で済む)。
    /// </summary>
    public IEnumerable<string> UriFilters()
    {
        // "*://*<domain>/*" は apex(https://doubleclick.net/x) と
        // サブドメイン(https://ads.doubleclick.net/x) の両方に一致する
        foreach (var d in _domains) yield return "*://*" + d + "/*";
    }

    public bool ShouldBlock(string uri)
    {
        if (_domains.Count == 0) return false;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return false;
        var host = u.Host;
        foreach (var (probeHost, probePath) in DetectionProbes)
            if (host.Equals(probeHost, StringComparison.OrdinalIgnoreCase) &&
                u.AbsolutePath.Equals(probePath, StringComparison.Ordinal))
                return false;

        // host 自身と親ドメインを順に照合 (ads.example.com → example.com → com)
        for (int i = 0; i >= 0 && i < host.Length;)
        {
            if (_domains.Contains(i == 0 ? host : host[i..])) return true;
            int dot = host.IndexOf('.', i);
            if (dot < 0) break;
            i = dot + 1;
        }
        return false;
    }

    const string DefaultList = """
# Karu 広告・トラッカー ブロックリスト
# 1行1ドメイン。サブドメインも自動的に対象。# で始まる行はコメント。
# サイトが壊れたら該当行を消して Karu を再起動してください。

# --- 広告ネットワーク ---
doubleclick.net
googlesyndication.com
googleadservices.com
adservice.google.com
googletagservices.com
adnxs.com
adsrvr.org
criteo.com
criteo.net
taboola.com
outbrain.com
pubmatic.com
rubiconproject.com
openx.net
indexexchange.com
casalemedia.com
smartadserver.com
teads.tv
adroll.com
amazon-adsystem.com
media.net
33across.com
sharethrough.com
triplelift.com
bidswitch.net
mgid.com
revcontent.com
yieldmo.com
sonobi.com
gumgum.com
springserve.com
adform.net
flashtalking.com
serving-sys.com
contextweb.com
lijit.com
spotxchange.com
spotx.tv
improvedigital.com
themediagrid.com
zemanta.com
2mdn.net
mathtag.com
bidr.io
w55c.net
stickyadstv.com
smartclip.net
emxdgt.com
districtm.io
undertone.com
adcolony.com
applovin.com
inmobi.com
smaato.net

# --- 国内広告 (SSP/アドネットワーク) ---
socdm.com
adingo.jp
fluct.jp
i-mobile.co.jp
microad.jp
geniee.co.jp
gsspat.jp
gssprt.jp
im-apps.net
nend.net
zucks.net
ad-stir.com
deqwas.net
logly.co.jp
popin.cc
yads.c.yimg.jp
cxense.com
ad-m.asia
advg.jp
xrost.jp

# --- アクセス解析・行動トラッキング ---
google-analytics.com
googletagmanager.com
scorecardresearch.com
quantserve.com
moatads.com
doubleverify.com
adsafeprotected.com
hotjar.com
mixpanel.com
amplitude.com
fullstory.com
mouseflow.com
clarity.ms
heapanalytics.com
newrelic.com
nr-data.net
branch.io
appsflyer.com
adjust.com
segment.io
segment.com
chartbeat.com
chartbeat.net
statcounter.com
crazyegg.com
luckyorange.com
inspectlet.com
smartlook.com
optimizely.com
bounceexchange.com
parsely.com
analytics.tiktok.com
mc.yandex.ru

# --- データ管理基盤 (DMP) / ID同期 ---
demdex.net
omtrdc.net
2o7.net
everesttech.net
krxd.net
bluekai.com
crwdcntrl.net
exelator.com
rlcdn.com
agkn.com
tapad.com
eyeota.net
id5-sync.com
adsymptotic.com
""";
}
