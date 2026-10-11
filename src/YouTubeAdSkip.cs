using System.Globalization;
using System.Text.Json;

namespace Karu;

/// <summary>
/// YouTube広告スキップのホスト側ヘルパー(純粋関数。テスト対象)。
/// 注入スクリプト(<see cref="Injections.YouTube"/>)が見えているスキップボタンの中心座標を
/// "adSkip|x|y" で送ってくるので、それを検証し、CDP Input.dispatchMouseEvent のパラメータ列に変える。
/// </summary>
static class YouTubeAdSkip
{
    public const string Command = "adSkip";

    /// <summary>ビューポート座標の上限(CSSピクセル)。これを超える値は壊れた/偽装されたコマンドとみなす。</summary>
    const int MaxCoordinate = 16384;

    /// <summary>"adSkip|x|y" を解析する。座標は非負の整数(CSSピクセル・ビューポート基準)のみ受け付ける。</summary>
    public static bool TryParseCommand(string cmd, out int x, out int y)
    {
        x = y = 0;
        var parts = cmd.Split('|');
        if (parts.Length != 3 || parts[0] != Command) return false;
        // NumberStyles.None: 符号・空白・小数点を許さない
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out x)) return false;
        if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out y)) return false;
        return x <= MaxCoordinate && y <= MaxCoordinate;
    }

    /// <summary>youtube.com とそのサブドメインだけを通す(末尾一致の偽装ホストは弾く)。</summary>
    public static bool IsYouTubeHost(string host) =>
        host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 本物のクリックを構成する CDP Input.dispatchMouseEvent のパラメータ列(押下→解放)。
    /// 座標はCSSピクセルのビューポート基準で、getBoundingClientRect の値をそのまま使える
    /// (150%/100% の混在DPIで実測、換算不要)。mouseMoved は入れない: 非表示のWebView(背景タブ)では
    /// mouseMoved が完了せず待ち続けるが、押下→解放だけでも isTrusted=true のクリックが届く
    /// (WebView2 154.0.4258.62 で実測、2026-10-11)。
    /// </summary>
    public static IEnumerable<string> ClickSequence(int x, int y)
    {
        yield return JsonSerializer.Serialize(new { type = "mousePressed", x, y, button = "left", clickCount = 1 });
        yield return JsonSerializer.Serialize(new { type = "mouseReleased", x, y, button = "left", clickCount = 1 });
    }
}
