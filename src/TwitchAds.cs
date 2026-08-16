using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace Karu;

/// <summary>
/// Twitch のサーバー側広告挿入 (SSAI) への対処。
///
/// Twitch は広告を配信本体と同じ HLS ストリームに縫い込んで同一ドメインから配るため、
/// ドメイン単位のブロック(AdBlocker)では原理的に触れない。実測して分かった性質を使う:
///
///   ・広告区間はメディアプレイリストの #EXT-X-DATERANGE ... CLASS="twitch-stitched-ad" で分かる
///   ・**広告は「ブレイク発生時点で既に確立していたプレイリスト」にだけ縫い込まれる**。
///     ブレイクの最中に取り直したプレイリストは素の本編が返る
///   ・**マスタープレイリスト(usher)のURLは叩き直すたびに新しいセッションを返す**ので、
///     プレイヤーが使った usher URL を覚えておいて再取得すればそれだけで「取り直し」になる
///   ・セッションが違ってもセグメントの実体は共通 (media-sequence も一致) なので、
///     プレイリストを差し替えてもタイムラインは揃ったまま繋がる
///
/// そこでメディアプレイリストの取得を横取りし、広告が入っていたら usher を叩き直して
/// 同じ画質のプレイリストを取り、その中身を代わりに返す。プレイヤーからは何も変わって見えない。
///
/// **GQL(PlaybackAccessToken)は使わない**: usher URL の再取得で足りるため、Client-ID や
/// persisted query のハッシュといった壊れやすい依存を持たずに済む。
///
/// 取り直し先も広告を引くことがある (プリロールの頻度キャップ次第) ので数回試し、
/// それでも駄目なら諦めて元の中身を返す — その場合はページ側で消音+被いを出す。
/// </summary>
sealed class TwitchAds
{
    const string AdMarker = "CLASS=\"twitch-stitched-ad\"";

    /// <summary>取り直しを試す回数。差し替え先がプリロールを引いたときの再試行。</summary>
    const int SwapAttempts = 3;

    /// <summary>覚えておくプレイリストの上限 (長時間の視聴で辞書が際限なく育たないように)。</summary>
    const int MaxTracked = 512;

    // 圧縮応答をそのまま文字列化して壊さないよう自動展開を有効にする
    static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(8),
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
    };

    // 画質名の入る属性はマスターの版で違う:
    //   v1 (usher/api/channel/hls/...)    → VIDEO="720p60"
    //   v2 (usher/api/v2/channel/hls/...) → STABLE-VARIANT-ID="720p60"  ※実プレイヤーはこちら
    static readonly Regex NameRe =
        new(@"(?:VIDEO|STABLE-VARIANT-ID)=""([^""]+)""", RegexOptions.Compiled);

    /// <summary>メディアプレイリストURL → どの usher から来た何番目のどの画質か。</summary>
    readonly ConcurrentDictionary<string, Variant> _origin = new();

    /// <summary>差し替え済みの対応表。プレイヤーが要求するURL → 実際に読みに行くURL。</summary>
    readonly ConcurrentDictionary<string, string> _swapped = new();

    readonly record struct Variant(string Usher, string Name, int Index);

    public static bool IsUsher(string uri)
        => uri.Contains("usher.ttvnw.net/api/", StringComparison.Ordinal);

    public static bool IsMediaPlaylist(string uri)
        => uri.Contains(".playlist.ttvnw.net/v1/playlist/", StringComparison.Ordinal);

    public static bool HasAd(string playlist)
        => playlist.Contains(AdMarker, StringComparison.Ordinal);

    /// <summary>
    /// usher(マスタープレイリスト)の応答。中身はそのまま返しつつ、
    /// 「どのURLが何番目のどの画質か」を覚えて後の取り直しに使う。
    /// </summary>
    public async Task<string?> InterceptUsherAsync(string uri)
    {
        var body = await GetAsync(uri);
        if (body is not null && body.StartsWith("#EXTM3U", StringComparison.Ordinal))
            Remember(uri, body);
        return body;
    }

    /// <summary>
    /// メディアプレイリストの応答。広告が入っていたら取り直した中身に差し替えて返す。
    /// 戻り値の Blocked は「広告を検知したが差し替えられなかった」= ページ側で消音+被いに回す合図。
    /// null は「取得できなかったので横取りをやめる」= 通常どおりネットワークへ流す。
    /// </summary>
    public async Task<(string Body, bool Blocked)?> InterceptPlaylistAsync(string uri)
    {
        // 一度差し替えたURLは以後そちらを読み続ける (元は広告で止まったままになる)。
        // 差し替え先が次のブレイクで広告に入ったら、またその時点で取り直す。
        var target = _swapped.TryGetValue(uri, out var t) ? t : uri;

        var body = await GetAsync(target);
        if (body is null && target != uri)
        {
            // 差し替え先が切れていたら元へ戻してやり直す
            _swapped.TryRemove(uri, out _);
            target = uri;
            body = await GetAsync(target);
        }
        if (body is null) return null;
        if (!HasAd(body)) return (body, false);

        if (!_origin.TryGetValue(uri, out var want) && !_origin.TryGetValue(target, out want))
            return (body, true);   // どの画質か分からないので差し替えられない

        for (int i = 0; i < SwapAttempts; i++)
        {
            var master = await GetAsync(want.Usher);   // 叩き直すだけで新しいセッションになる
            if (master is null || !master.StartsWith("#EXTM3U", StringComparison.Ordinal)) break;
            Remember(want.Usher, master);

            var fresh = Pick(master, want);
            if (fresh is null) break;
            var freshBody = await GetAsync(fresh);
            if (freshBody is null || !freshBody.StartsWith("#EXTM3U", StringComparison.Ordinal)) continue;
            if (HasAd(freshBody)) continue;            // 取り直し先もプリロールを引いた → もう一度

            _swapped[uri] = fresh;
            return (freshBody, false);
        }
        return (body, true);
    }

    /// <summary>マスタープレイリストを解析して URL→(usher, 画質名, 並び順) を覚える。</summary>
    void Remember(string usher, string master)
    {
        if (_origin.Count > MaxTracked) _origin.Clear();
        string name = "";
        int index = 0;
        foreach (var raw in master.Split((char)10))
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal))
            {
                var m = NameRe.Match(line);
                name = m.Success ? m.Groups[1].Value : "";
            }
            else if (line.StartsWith("http", StringComparison.Ordinal))
            {
                _origin[line] = new Variant(usher, name, index++);
                name = "";
            }
        }
    }

    /// <summary>
    /// 取り直したマスターから、元と同じ画質のURLを選ぶ。
    /// 同じ usher URL を叩き直しているので並び順は安定している。名前が一致しなければ
    /// 同じ並び順、それも無ければ先頭にフォールバックする。
    /// </summary>
    static string? Pick(string master, Variant want)
    {
        var urls = new List<(string Name, string Url)>();
        string name = "";
        foreach (var raw in master.Split((char)10))
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal))
            {
                var m = NameRe.Match(line);
                name = m.Success ? m.Groups[1].Value : "";
            }
            else if (line.StartsWith("http", StringComparison.Ordinal))
            {
                urls.Add((name, line));
                name = "";
            }
        }
        if (urls.Count == 0) return null;
        if (want.Name.Length > 0)
            foreach (var u in urls)
                if (u.Name == want.Name) return u.Url;
        if (want.Index < urls.Count) return urls[want.Index].Url;
        return urls[0].Url;
    }

    static async Task<string?> GetAsync(string uri)
    {
        try
        {
            using var res = await Http.GetAsync(uri);
            if (!res.IsSuccessStatusCode) return null;
            return await res.Content.ReadAsStringAsync();
        }
        catch { return null; }
    }
}
