using System.IO;

namespace Karu;

/// <summary>
/// ドメイン単位の簡易広告・トラッカーブロッカー。
/// %APPDATA%\Karu\blocklist.txt（1行1ドメイン、# はコメント）を読み込み、
/// リクエスト先ホストがリスト内ドメインまたはそのサブドメインなら遮断する。
/// </summary>
public class AdBlocker
{
    static readonly string FilePath = Path.Combine(Paths.AppDataDir, "blocklist.txt");

    readonly HashSet<string> _domains = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _domains.Count;

    public void Load()
    {
        if (!File.Exists(FilePath))
        {
            try { File.WriteAllText(FilePath, DefaultList); } catch { }
        }
        _domains.Clear();
        try
        {
            foreach (var raw in File.ReadAllLines(FilePath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                _domains.Add(line);
            }
        }
        catch { }
    }

    public bool ShouldBlock(string uri)
    {
        if (_domains.Count == 0) return false;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return false;
        var host = u.Host;

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
# サイトが壊れたら該当行を消すか、ツールバーの盾ボタンで一時的にOFFにできます。

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
""";
}
