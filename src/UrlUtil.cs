using System.Text.RegularExpressions;

namespace Karu;

static class UrlUtil
{
    // ドット無しでも "host:8080" 形式はURLとして扱う (開発サーバー等)
    static readonly Regex HostPortRe = new(@"^[a-zA-Z0-9_-]+:\d+(/.*)?$", RegexOptions.Compiled);
    static readonly Regex Ipv6Re = new(@"^\[[0-9a-fA-F:]+\](?::\d+)?(?:/.*)?$", RegexOptions.Compiled);

    public static string Normalize(string input)
    {
        input = input.Trim();
        if (Uri.TryCreate(input, UriKind.Absolute, out var absolute))
        {
            // javascript:/data:/未知のschemeをアドレス欄や起動引数から実行させない。
            // 通常閲覧とローカルHTMLに必要なschemeだけを通す。
            if ((absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps) &&
                !string.IsNullOrEmpty(absolute.Host))
                return absolute.AbsoluteUri;
            if (absolute.Scheme == Uri.UriSchemeFile) return absolute.AbsoluteUri;
            if (input.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) return "about:blank";
        }
        if (!input.Contains(' '))
        {
            if (input.Contains('.')) return "https://" + input;
            // ローカル開発サーバーはhttpsを持たないことが多いので http で開く
            if (HostPortRe.IsMatch(input) || Ipv6Re.IsMatch(input) || input == "localhost" ||
                input.StartsWith("localhost/", StringComparison.Ordinal))
                return "http://" + input;
        }
        return "https://www.google.com/search?q=" + Uri.EscapeDataString(input);
    }
}
