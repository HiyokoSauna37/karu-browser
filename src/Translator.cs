using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Karu;

/// <summary>
/// ページ翻訳(「このページを翻訳」相当)に使う軽量翻訳クライアント。
///
/// Google 翻訳の無料 gtx エンドポイント(公開・認証不要。多くの翻訳ツールが使う)をホスト側の
/// HttpClient で叩く。ページ内 fetch だと CORS で弾かれるため、ネットワークはホストで行い、
/// 結果だけ WebView2 へ渡す(DOM 走査・置換は注入 JS 側)。非公式 API なのでレート制限や仕様変更で
/// 失敗しうる — 失敗したバッチのセグメントは未翻訳("")のまま残し Partial=true で返す。
/// </summary>
static class Translator
{
    static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
        // 並列リクエストを 1 本の TLS コネクションへ多重化する(接続確立コストを 1 回に抑える)
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
    };
    const string Base = "https://translate.googleapis.com/translate_a/single";

    /// <summary>1 リクエストにまとめる q 文字列の目安上限。q は POST ボディで送るため URL 長の制約は
    /// なく、gtx が改行境界を保てる実測範囲(5KB 強・45 行まで確認)に余裕を持たせた値。</summary>
    const int BatchBudget = 4000;

    /// <summary>同時に投げるリクエスト数の上限(直列待ちをなくしつつレート制限を刺激しない程度)。</summary>
    const int MaxParallel = 6;

    /// <summary>送る翻訳リクエストの上限。超えたら以降は未翻訳のまま残し Partial=true にする。</summary>
    const int MaxRequests = 80;

    /// <summary>
    /// 複数セグメントをまとめて翻訳し、入力と 1:1 に対応する訳文配列を返す。
    /// 小さいセグメントは改行連結で 1 リクエストにまとめ(gtx は改行境界を保つので split で復元)、
    /// バッチは MaxParallel 本まで並列で投げる。行数がずれたバッチだけ 1 セグメントずつ翻訳し直して
    /// 整合性を担保する。onProgress を渡すと 1 バッチ完了するたびに途中経過の配列(戻り値と同じ
    /// インスタンス。未完了セグメントは "")で呼ぶ — 届いた分から順次 DOM へ反映する用。
    /// </summary>
    public static async Task<(string[] Translations, string? Detected, bool Partial)> TranslateAsync(
        string[] segments, string to = "ja", string from = "auto",
        Func<string[], Task>? onProgress = null)
    {
        var translations = new string[segments.Length];
        for (int i = 0; i < translations.Length; i++) translations[i] = "";
        string? detected = null;
        int requests = 0;
        bool partial = false;
        var sync = new object();

        void Note(string? d) { if (d != null) lock (sync) detected ??= d; }
        // 1 リクエスト分の予算を確保。使い切っていたら partial にして false
        bool TryBook()
        {
            if (Interlocked.Increment(ref requests) <= MaxRequests) return true;
            partial = true;
            return false;
        }

        // バッチ割り: 予算内で改行連結できるものは batches へ、単体で予算超過のものは longs へ
        var batches = new List<List<int>>();
        var longs = new List<int>();
        var cur = new List<int>();
        int curLen = 0;
        for (int i = 0; i < segments.Length; i++)
        {
            var seg = segments[i].Trim();
            if (seg.Length == 0) continue;
            if (seg.Length + 1 > BatchBudget) { longs.Add(i); continue; }
            if (curLen + seg.Length + 1 > BatchBudget && cur.Count > 0)
            {
                batches.Add(cur);
                cur = new List<int>();
                curLen = 0;
            }
            cur.Add(i);
            curLen += seg.Length + 1;
        }
        if (cur.Count > 0) batches.Add(cur);

        async Task RunBatchAsync(List<int> idxs)
        {
            await Gate.WaitAsync();
            try
            {
                if (!TryBook()) return;
                var joined = string.Join("\n", idxs.Select(i => segments[i].Trim()));
                var (text, d) = await ChunkAsync(joined, to, from);
                Note(d);
                var parts = text.Split('\n');
                if (parts.Length == idxs.Count)
                {
                    for (int k = 0; k < idxs.Count; k++) translations[idxs[k]] = parts[k];
                }
                else
                {
                    // 改行境界が崩れた(訳で行が増減した)場合は 1 セグメントずつ翻訳し直す
                    foreach (var i in idxs)
                    {
                        if (!TryBook()) return;
                        var (t2, d2) = await ChunkAsync(segments[i].Trim(), to, from);
                        translations[i] = t2;
                        Note(d2);
                    }
                }
            }
            catch { partial = true; } // このバッチだけ未翻訳で残し、他のバッチは続行する
            finally { Gate.Release(); }
        }

        // 予算超過の 1 セグメントは空白境界で分割して翻訳し、全スライス成功時のみ採用する
        // (途中で失敗すると原文と訳文が混ざったブロックになるため)
        async Task RunLongAsync(int idx)
        {
            await Gate.WaitAsync();
            try
            {
                var slices = new List<string>();
                var rest = segments[idx].Trim();
                while (rest.Length > BatchBudget)
                {
                    int cut = rest.LastIndexOf(' ', BatchBudget);
                    if (cut <= 0) cut = BatchBudget;
                    slices.Add(rest[..cut]);
                    rest = rest[cut..];
                }
                if (rest.Length > 0) slices.Add(rest);

                var sb = new StringBuilder();
                foreach (var s in slices)
                {
                    if (!TryBook()) return;
                    var (t, d) = await ChunkAsync(s, to, from);
                    sb.Append(t);
                    Note(d);
                }
                translations[idx] = sb.ToString();
            }
            catch { partial = true; }
            finally { Gate.Release(); }
        }

        var pending = batches.Select(RunBatchAsync).Concat(longs.Select(RunLongAsync)).ToList();
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending);
            pending.Remove(done);
            // 最後の 1 バッチは呼び出し側の最終適用に任せる(二重適用を避ける)
            if (onProgress != null && pending.Count > 0) await onProgress(translations);
        }

        return (translations, detected, partial);
    }

    /// <summary>並列度の上限ゲート(プロセス内で共有 — 複数タブ同時翻訳でも合計 MaxParallel 本)。</summary>
    static readonly SemaphoreSlim Gate = new(MaxParallel);

    /// <summary>gtx エンドポイントへ 1 リクエスト送り、翻訳文と検出言語を返す。</summary>
    static async Task<(string Text, string? Detected)> ChunkAsync(string text, string to, string from)
    {
        var url = $"{Base}?client=gtx&sl={Uri.EscapeDataString(from)}&tl={Uri.EscapeDataString(to)}&dt=t";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent("q=" + Uri.EscapeDataString(text), Encoding.UTF8,
                                        "application/x-www-form-urlencoded"),
        };
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        using var res = await Http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadAsStringAsync();

        // 応答は [ [[訳, 原文, ...], ...], ..., 検出言語, ... ]。data[0] の各要素の [0] を連結すると全文になる。
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var sb = new StringBuilder();
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
        {
            var rows = root[0];
            if (rows.ValueKind == JsonValueKind.Array)
                foreach (var row in rows.EnumerateArray())
                    if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() > 0 &&
                        row[0].ValueKind == JsonValueKind.String)
                        sb.Append(row[0].GetString());
        }
        string? detected = null;
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 2 &&
            root[2].ValueKind == JsonValueKind.String)
            detected = root[2].GetString();
        return (sb.ToString(), detected);
    }
}
