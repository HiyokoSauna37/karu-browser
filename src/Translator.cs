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
/// 失敗しうる — その場合は例外を投げる(呼び出し側で握る)。
/// </summary>
static class Translator
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    const string Base = "https://translate.googleapis.com/translate_a/single";

    /// <summary>1 リクエストにまとめる q 文字列の目安上限(URL 長・API 側の上限に余裕を持たせる)。</summary>
    const int BatchBudget = 1500;

    /// <summary>送る翻訳リクエストの上限。超えたら以降は未翻訳のまま残し partial=true にする。</summary>
    const int MaxRequests = 80;

    /// <summary>
    /// 複数セグメントをまとめて翻訳し、入力と 1:1 に対応する訳文配列を返す。
    /// 小さいセグメントは改行連結で 1 リクエストにまとめ(gtx は改行境界を保つので split で復元)、
    /// 行数がずれたバッチだけ 1 セグメントずつ翻訳し直して整合性を担保する。
    /// </summary>
    public static async Task<(string[] Translations, string? Detected, bool Partial)> TranslateAsync(
        string[] segments, string to = "ja", string from = "auto")
    {
        var translations = new string[segments.Length];
        for (int i = 0; i < translations.Length; i++) translations[i] = "";
        string? detected = null;
        int requests = 0;
        bool partial = false;

        var targets = new List<int>();
        for (int i = 0; i < segments.Length; i++)
            if (segments[i].Trim().Length > 0) targets.Add(i);

        var batch = new List<int>();
        int batchLen = 0;

        async Task FlushAsync()
        {
            if (batch.Count == 0) return;
            var cur = batch;
            batch = new List<int>();
            batchLen = 0;
            requests++;
            var joined = string.Join("\n", cur.Select(i => segments[i].Trim()));
            var (text, d) = await ChunkAsync(joined, to, from);
            if (d != null && detected == null) detected = d;
            var parts = text.Split('\n');
            if (parts.Length == cur.Count)
            {
                for (int k = 0; k < cur.Count; k++) translations[cur[k]] = parts[k];
            }
            else
            {
                // 改行境界が崩れた(訳で行が増減した)場合は 1 セグメントずつ翻訳し直す
                foreach (var i in cur)
                {
                    if (requests >= MaxRequests) { partial = true; break; }
                    requests++;
                    var (t2, d2) = await ChunkAsync(segments[i].Trim(), to, from);
                    translations[i] = t2;
                    if (d2 != null && detected == null) detected = d2;
                }
            }
        }

        foreach (var i in targets)
        {
            if (requests >= MaxRequests) { partial = true; break; }
            var seg = segments[i].Trim();
            if (seg.Length + 1 > BatchBudget)
            {
                // 単体で予算超過 → 溜まっているバッチを吐いてから、この 1 件だけ分割翻訳する
                await FlushAsync();
                if (requests >= MaxRequests) { partial = true; break; }
                var (t, d) = await LongAsync(seg, to, from, () => requests++);
                translations[i] = t;
                if (d != null && detected == null) detected = d;
                continue;
            }
            if (batchLen + seg.Length + 1 > BatchBudget && batch.Count > 0) await FlushAsync();
            batch.Add(i);
            batchLen += seg.Length + 1;
        }
        if (requests < MaxRequests) await FlushAsync();
        else if (batch.Count > 0) partial = true;

        return (translations, detected, partial);
    }

    /// <summary>gtx エンドポイントへ 1 リクエスト送り、翻訳文と検出言語を返す。</summary>
    static async Task<(string Text, string? Detected)> ChunkAsync(string text, string to, string from)
    {
        var url = $"{Base}?client=gtx&sl={Uri.EscapeDataString(from)}&tl={Uri.EscapeDataString(to)}" +
                  $"&dt=t&q={Uri.EscapeDataString(text)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
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

    /// <summary>予算を超える 1 セグメントを空白境界で分割して翻訳し、連結して返す。</summary>
    static async Task<(string Text, string? Detected)> LongAsync(
        string text, string to, string from, Action bump)
    {
        var slices = new List<string>();
        var rest = text;
        while (rest.Length > BatchBudget)
        {
            int cut = rest.LastIndexOf(' ', BatchBudget);
            if (cut <= 0) cut = BatchBudget;
            slices.Add(rest[..cut]);
            rest = rest[cut..];
        }
        if (rest.Length > 0) slices.Add(rest);

        var outSb = new StringBuilder();
        string? detected = null;
        foreach (var s in slices)
        {
            bump();
            var (t, d) = await ChunkAsync(s, to, from);
            outSb.Append(t);
            if (d != null && detected == null) detected = d;
        }
        return (outSb.ToString(), detected);
    }
}
